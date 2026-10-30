using System.Buffers.Binary;
using Cheatmaster.Core.Memory;

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
/// It works through the system's own debugging interface (the Win32 debug API, or ptrace on
/// Linux) and the four hardware watchpoints the processor
/// provides, so nothing is written into the target and no code is injected. The cost is honest:
/// attaching a debugger is intrusive, each trap freezes the target until we continue it, and
/// some games notice they are being debugged.
/// </summary>
public sealed partial class AccessWatch : IDisposable
{
    private readonly TargetProcess _process;
    private readonly AccessWatchOptions _options;
    private readonly List<WatchSlot> _slots;
    private readonly Dictionary<ulong, SiteState> _sites = [];
    private readonly List<(string Name, ulong Value)> _registerBuffer = [];
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Lock _gate = new();

    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _running;
    private volatile string _status = string.Empty;
    private string _startError = string.Empty;
    private ulong _lastValue;
    private int _hits;

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

        // Both debugging interfaces are thread-affine: whichever thread attaches is the only one
        // allowed to wait for events or to detach. It gets a thread of its own, never a pooled one.
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Cheatmaster watchpoint"
        };
        _thread.Start();
        _started.Wait(5000);
    }

    private void Run()
    {
        if (OperatingSystem.IsWindows()) RunWindows();
        else if (OperatingSystem.IsLinux()) RunLinux();
        else
        {
            _startError = "Watching an address is not supported on this system.";
            _started.Set();
        }
    }

    /// <summary>Files one hit. The caller has already put the thread's registers in <see cref="_registerBuffer"/>.</summary>
    private void Record(ulong ip, uint tid, ulong firedAddress)
    {
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
