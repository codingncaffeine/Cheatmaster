using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Native;

namespace Cheatmaster.Core.Debugging;

public sealed class AccessWatchOptions
{
    /// <summary>Writes only is quieter; reads and writes is what finds the code that displays a value.</summary>
    public WatchOn On { get; set; } = WatchOn.ReadOrWrite;

    /// <summary>
    /// Every trap freezes the whole game until we continue it, so a watch cannot be left running
    /// forever on a busy address. Watching stops on its own once this many hits are in.
    /// </summary>
    public int MaxHits { get; set; } = 200;

    /// <summary>How far into a structure a field may sit, when working out which register is the base.</summary>
    public int MaxOffset { get; set; } = 0x1000;

    /// <summary>How many bytes of code to capture around each instruction.</summary>
    public int CodeWindow { get; set; } = 32;
}

/// <summary>
/// Watches an address and reports which instructions touch it.
///
/// This is the answer to the question a scan cannot settle: a search leaves several candidate
/// addresses and nothing says which one the game actually uses. Code that reads or writes an
/// address proves it, and the register state at that moment usually hands over the object the
/// value belongs to as well.
///
/// It works through the real debugging API and the four hardware watchpoints the processor
/// provides, so nothing is written into the target and no code is injected. The cost is honest:
/// attaching a debugger is intrusive, each trap freezes the target until we continue it, and
/// some games notice they are being debugged.
/// </summary>
public sealed class AccessWatch : IDisposable
{
    private const int DebugEventSize = 176;
    private const int UnionOffset = 16;
    private const int FirstChanceOffset = 168;

    private readonly TargetProcess _process;
    private readonly AccessWatchOptions _options;
    private readonly List<WatchSlot> _slots;
    private readonly Dictionary<uint, nint> _threads = [];
    private readonly Dictionary<ulong, SiteState> _sites = [];
    private readonly List<(string Name, ulong Value)> _registerBuffer = [];
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Lock _gate = new();

    private ThreadContextBuffer? _context;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _running;
    private volatile string _status = string.Empty;
    private string _startError = string.Empty;
    private ulong _lastValue;
    private int _hits;
    private bool _swept;

    private AccessWatch(TargetProcess process, ulong address, int width, AccessWatchOptions options, List<WatchSlot> slots)
    {
        _process = process;
        Address = address;
        Width = width;
        _options = options;
        _slots = slots;
    }

    public ulong Address { get; }

    public int Width { get; }

    /// <summary>The aligned spans actually being watched. Four is all the hardware has.</summary>
    public IReadOnlyList<WatchSlot> Slots => _slots;

    /// <summary>False when the value sits too awkwardly for four watchpoints to cover all of it.</summary>
    public bool CoversWholeValue => DebugRegisters.Covers(_slots, Address, Width);

    public bool IsRunning => _running;

    public int HitCount => Volatile.Read(ref _hits);

    public string Status => _status;

    /// <summary>
    /// Attaches and starts watching. Returns null with a reason when the target cannot be
    /// debugged, which is what happens with anything protected or already under a debugger.
    /// </summary>
    public static AccessWatch? Start(TargetProcess process, ulong address, int width,
        AccessWatchOptions? options, out string error)
    {
        error = string.Empty;
        options ??= new AccessWatchOptions();

        if (width is < 1 or > 8)
        {
            error = "A watchpoint can only cover between one and eight bytes.";
            return null;
        }

        if (!process.IsRunning)
        {
            error = "The game is no longer running.";
            return null;
        }

        // A 32-bit target runs in compatibility mode, where the eight-byte watch length is not
        // dependable across processors; four-byte spans are.
        var slots = DebugRegisters.Plan(address, width, process.Is64Bit ? 8 : 4);
        if (slots.Count == 0)
        {
            error = "That address cannot be covered by a watchpoint.";
            return null;
        }

        var watch = new AccessWatch(process, address, width, options, slots);
        watch.Launch();

        if (!watch._running)
        {
            error = watch._startError.Length > 0 ? watch._startError : "The debugger did not start.";
            watch.Dispose();
            return null;
        }

        return watch;
    }

    private void Launch()
    {
        _lastValue = ReadWatchedValue(0);

        // The Win32 debugging API is thread-affine: whichever thread attaches is the only one
        // allowed to wait for events or to detach. It gets a thread of its own, never a pooled one.
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Cheatmaster watchpoint"
        };
        _thread.Start();
        _started.Wait(5000);
    }

    private unsafe void Run()
    {
        uint pid = (uint)_process.Pid;

        if (!DebugApi.DebugActiveProcess(pid))
        {
            _startError = DescribeAttachFailure(Marshal.GetLastPInvokeError());
            _started.Set();
            return;
        }

        // Immediately, and before anything else can go wrong: the default is that a debuggee is
        // killed when its debugger detaches or dies, which would take the game down with us.
        DebugApi.DebugSetProcessKillOnExit(false);

        _context = new ThreadContextBuffer(_process.Is64Bit);
        _running = true;
        _status = "Watching. Play the game and make the value change.";
        _started.Set();

        byte* ev = (byte*)NativeMemory.AlignedAlloc(DebugEventSize, 16);
        NativeMemory.Clear(ev, DebugEventSize);
        bool targetGone = false;

        try
        {
            targetGone = Loop(ev);
        }
        catch (Exception ex)
        {
            _status = "The watch stopped: " + ex.Message;
        }
        finally
        {
            if (!targetGone)
            {
                // Detaching with the watchpoints still armed would leave the game trapping into a
                // debugger that is no longer there, which kills it on the very next access.
                DisarmAll();
                DebugApi.DebugActiveProcessStop(pid);
            }

            CloseThreads();
            NativeMemory.AlignedFree(ev);
            _context.Dispose();
            _context = null;
            _running = false;
        }
    }

    /// <summary>Returns true when the target exited, meaning there is nothing left to detach from.</summary>
    private unsafe bool Loop(byte* ev)
    {
        while (!_stop)
        {
            if (!DebugApi.WaitForDebugEventEx(ev, 100))
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == DebugApi.WaitTimedOut) continue;
                _status = $"The debug loop ended with Win32 error {err}.";
                return false;
            }

            uint code = *(uint*)ev;
            uint eventPid = *(uint*)(ev + 4);
            uint tid = *(uint*)(ev + 8);
            uint verdict = DebugStatus.Continue;
            bool exited = false;

            switch (code)
            {
                case DebugEventCode.CreateProcess:
                    CloseEventFile(ev);
                    ArmThread(tid);
                    break;

                case DebugEventCode.CreateThread:
                    // A game that spawns a worker after we attached would otherwise touch the
                    // address without ever tripping a watchpoint.
                    ArmThread(tid);
                    break;

                case DebugEventCode.ExitThread:
                    ForgetThread(tid);
                    break;

                case DebugEventCode.LoadDll:
                    CloseEventFile(ev);
                    break;

                case DebugEventCode.ExitProcess:
                    _status = "The game closed.";
                    exited = true;
                    break;

                case DebugEventCode.Exception:
                    verdict = HandleException(ev, tid);
                    break;
            }

            DebugApi.ContinueDebugEvent(eventPid, tid, verdict);

            if (exited) return true;

            if (HitCount >= _options.MaxHits)
            {
                _status = $"Stopped after {HitCount} hits, which is more than enough to tell what touches this.";
                return false;
            }
        }

        if (_status.Length == 0 || _status.StartsWith("Watching", StringComparison.Ordinal))
        {
            _status = HitCount == 0
                ? "Stopped. Nothing touched the address while it was being watched."
                : $"Stopped after {HitCount} hits.";
        }

        return false;
    }

    private unsafe uint HandleException(byte* ev, uint tid)
    {
        uint exceptionCode = *(uint*)(ev + UnionOffset);
        bool firstChance = *(uint*)(ev + FirstChanceOffset) != 0;

        if (exceptionCode == ExceptionCode.Breakpoint && !_swept)
        {
            // The breakpoint the system raises on attach, which is ours to swallow. It also
            // arrives after every existing thread has been reported, so this is the moment to
            // check that none of them were missed.
            _swept = true;
            SweepThreads();

            // Reads and writes are told apart by whether the value moved, so the reading starts
            // from here: anything written between attaching and arming would otherwise be charged
            // to whichever instruction happened to trap first.
            _lastValue = ReadWatchedValue(_lastValue);
            return DebugStatus.Continue;
        }

        // A hardware data watchpoint reports as a single step, not as a breakpoint.
        if (exceptionCode != ExceptionCode.SingleStep || !firstChance)
            return DebugStatus.ExceptionNotHandled;

        nint thread = ThreadHandle(tid);
        var context = _context;
        if (thread == 0 || context is null) return DebugStatus.ExceptionNotHandled;
        if (!context.Capture(thread, context.FullFlags)) return DebugStatus.ExceptionNotHandled;

        int slot = DebugRegisters.FiredSlot(context.Dr6);
        if (slot < 0 || slot >= _slots.Count)
            return DebugStatus.ExceptionNotHandled; // somebody else is single stepping; leave it alone

        // DR6 is sticky. Left set, it reports the same trap forever.
        context.Dr6 = 0;
        context.Apply(thread, context.DebugRegisterFlags);

        Record(context, tid, _slots[slot].Address);
        return DebugStatus.Continue;
    }

    private void Record(ThreadContextBuffer context, uint tid, ulong firedAddress)
    {
        ulong ip = context.InstructionPointer;
        context.ReadRegisters(_registerBuffer);

        var registers = new RegisterValue[_registerBuffer.Count];
        for (int i = 0; i < registers.Length; i++)
            registers[i] = new RegisterValue(_registerBuffer[i].Name, _registerBuffer[i].Value);

        // The target is frozen while a debug event is pending, so this reads the value exactly as
        // the instruction left it.
        ulong value = ReadWatchedValue(_lastValue);
        var kind = value != _lastValue ? AccessKind.Write : AccessKind.Read;
        _lastValue = value;

        var (code, codeBase) = ReadCode(ip);

        var hit = new AccessHit
        {
            InstructionPointer = ip,
            ThreadId = (int)tid,
            Kind = kind,
            Address = firedAddress,
            Value = value,
            Registers = registers,
            Code = code,
            CodeBase = codeBase
        };

        lock (_gate)
        {
            if (!_sites.TryGetValue(ip, out var site))
            {
                site = new SiteState(ip, new BaseRanker(Address, _options.MaxOffset));
                _sites[ip] = site;
            }
            site.Add(hit);
        }

        Interlocked.Increment(ref _hits);
    }

    private ulong ReadWatchedValue(ulong fallback)
    {
        Span<byte> buffer = stackalloc byte[8];
        buffer.Clear();
        if (!_process.ReadExact(Address, buffer[..Width])) return fallback;
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    /// <summary>
    /// Bytes around the instruction. The pointer is left sitting after the instruction that just
    /// ran, so the interesting bytes are behind it; a few past it come along for context.
    /// </summary>
    private (byte[] Code, ulong Base) ReadCode(ulong ip)
    {
        int window = Math.Max(8, _options.CodeWindow);
        int trailing = Math.Min(8, window / 4);
        ulong start = ip >= (ulong)(window - trailing) ? ip - (ulong)(window - trailing) : 0;

        byte[] buffer = new byte[window];
        int read = _process.Read(start, buffer);
        if (read <= 0) return ([], start);
        if (read < window) Array.Resize(ref buffer, read);
        return (buffer, start);
    }

    /// <summary>Every distinct instruction seen so far, busiest first.</summary>
    public List<AccessSite> Snapshot()
    {
        List<(ulong Ip, int Reads, int Writes, AccessHit Latest, List<BaseGuess> Bases)> raw;

        lock (_gate)
        {
            raw = new List<(ulong, int, int, AccessHit, List<BaseGuess>)>(_sites.Count);
            foreach (var site in _sites.Values)
            {
                if (site.Latest is null) continue;
                raw.Add((site.Ip, site.Reads, site.Writes, site.Latest, site.Ranker.Ranked()));
            }
        }

        // Naming the module an instruction lives in reads the target's module list, which has no
        // business happening while the debug thread is waiting on the lock with the game frozen.
        var result = new List<AccessSite>(raw.Count);
        foreach (var (ip, reads, writes, latest, bases) in raw)
        {
            var module = ModuleHolding(ip);
            result.Add(new AccessSite
            {
                InstructionPointer = ip,
                Module = module?.Name,
                ModuleOffset = module is null ? 0 : ip - module.Base,
                Reads = reads,
                Writes = writes,
                Latest = latest,
                Bases = bases
            });
        }

        result.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        return result;
    }

    private ModuleEntry? ModuleHolding(ulong address)
    {
        foreach (var module in _process.Modules)
        {
            if (address >= module.Base && address < module.End) return module;
        }
        return null;
    }

    private void SweepThreads()
    {
        nint snapshot = DebugApi.CreateToolhelp32Snapshot(DebugApi.SnapThread, 0);
        if (snapshot == 0 || snapshot == -1) return;

        try
        {
            uint size = (uint)Marshal.SizeOf<ThreadEntry32>();
            var entry = new ThreadEntry32 { Size = size };
            if (!DebugApi.Thread32First(snapshot, ref entry)) return;

            do
            {
                if (entry.OwnerProcessId == (uint)_process.Pid) ArmThread(entry.ThreadId);
                entry.Size = size;
            }
            while (DebugApi.Thread32Next(snapshot, ref entry));
        }
        finally
        {
            Win32.CloseHandle(snapshot);
        }
    }

    private nint ThreadHandle(uint tid)
    {
        if (_threads.TryGetValue(tid, out nint handle)) return handle;
        handle = DebugApi.OpenThread(ThreadAccess.ForBreakpoints, false, tid);
        if (handle != 0) _threads[tid] = handle;
        return handle;
    }

    private void ArmThread(uint tid)
    {
        nint handle = ThreadHandle(tid);
        if (handle == 0) return;
        // Only ever called while a debug event is pending, so the thread is already stopped.
        ApplyWatch(handle, arm: true, suspend: false);
    }

    private void ForgetThread(uint tid)
    {
        if (!_threads.Remove(tid, out nint handle)) return;
        Win32.CloseHandle(handle);
    }

    private void DisarmAll()
    {
        foreach (nint handle in _threads.Values)
            ApplyWatch(handle, arm: false, suspend: true);
    }

    private void CloseThreads()
    {
        foreach (nint handle in _threads.Values)
            Win32.CloseHandle(handle);
        _threads.Clear();
    }

    private void ApplyWatch(nint thread, bool arm, bool suspend)
    {
        var context = _context;
        if (context is null) return;

        // Changing the context of a running thread is undefined; while a debug event is pending
        // it is already stopped, and outside of that it has to be suspended by hand.
        if (suspend && DebugApi.SuspendThread(thread) == unchecked((uint)-1)) return;

        try
        {
            if (!context.Capture(thread, context.DebugRegisterFlags)) return;

            for (int i = 0; i < DebugRegisters.SlotCount; i++)
                context.SetDr(i, arm && i < _slots.Count ? _slots[i].Address : 0);

            context.Dr6 = 0;
            context.Dr7 = arm ? DebugRegisters.Control(_slots, _options.On) : 0;
            context.Apply(thread, context.DebugRegisterFlags);
        }
        finally
        {
            if (suspend) DebugApi.ResumeThread(thread);
        }
    }

    private static unsafe void CloseEventFile(byte* ev)
    {
        // The process-created and dll-loaded events both hand over a file handle that belongs to
        // the debugger to close.
        nint file = *(nint*)(ev + UnionOffset);
        if (file != 0 && file != -1) Win32.CloseHandle(file);
    }

    private static string DescribeAttachFailure(int err) => err switch
    {
        5 => Privileges.IsElevated
            ? "Access denied. The game is protected or running at a higher integrity level."
            : "Access denied. Restart Cheatmaster as administrator and try again.",
        87 => "Windows refused to attach. Something else is probably debugging this process already.",
        _ => $"Could not attach a debugger: Win32 error {err}."
    };

    public void Stop()
    {
        _stop = true;
        _thread?.Join(4000);
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        _started.Dispose();
    }

    private sealed class SiteState(ulong ip, BaseRanker ranker)
    {
        public ulong Ip { get; } = ip;
        public BaseRanker Ranker { get; } = ranker;
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public AccessHit? Latest { get; private set; }

        public void Add(AccessHit hit)
        {
            if (hit.Kind == AccessKind.Write) Writes++;
            else Reads++;
            Latest = hit;
            Ranker.Add(hit.Registers);
        }
    }
}
