using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cheatmaster.Core.Native;

namespace Cheatmaster.Core.Debugging;

/// <summary>The Windows half of <see cref="AccessWatch"/>: the Win32 debug loop and thread contexts.</summary>
public sealed partial class AccessWatch
{
    private const int DebugEventSize = 176;
    private const int UnionOffset = 16;
    private const int FirstChanceOffset = 168;

    private readonly Dictionary<uint, nint> _threads = [];
    private ThreadContextBuffer? _context;
    private bool _swept;

    [SupportedOSPlatform("windows")]
    private unsafe void RunWindows()
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
            targetGone = LoopWindows(ev);
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
    [SupportedOSPlatform("windows")]
    private unsafe bool LoopWindows(byte* ev)
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

    [SupportedOSPlatform("windows")]
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

        context.ReadRegisters(_registerBuffer);
        Record(context.InstructionPointer, tid, _slots[slot].Address);
        return DebugStatus.Continue;
    }

    [SupportedOSPlatform("windows")]
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

    [SupportedOSPlatform("windows")]
    private nint ThreadHandle(uint tid)
    {
        if (_threads.TryGetValue(tid, out nint handle)) return handle;
        handle = DebugApi.OpenThread(ThreadAccess.ForBreakpoints, false, tid);
        if (handle != 0) _threads[tid] = handle;
        return handle;
    }

    [SupportedOSPlatform("windows")]
    private void ArmThread(uint tid)
    {
        nint handle = ThreadHandle(tid);
        if (handle == 0) return;
        // Only ever called while a debug event is pending, so the thread is already stopped.
        ApplyWatch(handle, arm: true, suspend: false);
    }

    [SupportedOSPlatform("windows")]
    private void ForgetThread(uint tid)
    {
        if (!_threads.Remove(tid, out nint handle)) return;
        Win32.CloseHandle(handle);
    }

    [SupportedOSPlatform("windows")]
    private void DisarmAll()
    {
        foreach (nint handle in _threads.Values)
            ApplyWatch(handle, arm: false, suspend: true);
    }

    [SupportedOSPlatform("windows")]
    private void CloseThreads()
    {
        foreach (nint handle in _threads.Values)
            Win32.CloseHandle(handle);
        _threads.Clear();
    }

    [SupportedOSPlatform("windows")]
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

    [SupportedOSPlatform("windows")]
    private static unsafe void CloseEventFile(byte* ev)
    {
        // The process-created and dll-loaded events both hand over a file handle that belongs to
        // the debugger to close.
        nint file = *(nint*)(ev + UnionOffset);
        if (file != 0 && file != -1) Win32.CloseHandle(file);
    }

    [SupportedOSPlatform("windows")]
    private static string DescribeAttachFailure(int err) => err switch
    {
        5 => Privileges.IsElevated
            ? "Access denied. The game is protected or running at a higher integrity level."
            : "Access denied. Restart Cheatmaster as administrator and try again.",
        87 => "Windows refused to attach. Something else is probably debugging this process already.",
        _ => $"Could not attach a debugger: Win32 error {err}."
    };
}
