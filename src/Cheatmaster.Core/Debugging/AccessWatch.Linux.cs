using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cheatmaster.Core.Native.Linux;

namespace Cheatmaster.Core.Debugging;

/// <summary>
/// The Linux half of <see cref="AccessWatch"/>, built on ptrace.
///
/// Every thread of the game is seized (PTRACE_SEIZE, which unlike PTRACE_ATTACH sends no
/// signal), armed with the same four debug registers through PTRACE_POKEUSER, and let go again.
/// A hit stops only the thread that made it, as a SIGTRAP whose DR6 names the slot; anything
/// else that stops a thread is handed straight back to it, so the game sees its own signals
/// exactly as before. Threads the game starts while watched are followed automatically.
///
/// If Cheatmaster dies mid-watch the kernel detaches every thread and clears the watchpoints
/// itself, so a crash here never takes the game with it.
/// </summary>
public sealed partial class AccessWatch
{
    /// <summary>Thread id → armed yet. Only touched from the watch thread.</summary>
    private readonly Dictionary<int, bool> _tracees = [];
    private readonly ulong[] _regs = new ulong[Ptrace.RegisterBlockSize / 8];

    // user_regs_struct slot of each general register, in the order a listing names them.
    private static readonly (string Name64, string Name32, int Slot)[] LinuxRegisters =
    [
        ("RAX", "EAX", 10), ("RCX", "ECX", 11), ("RDX", "EDX", 12), ("RBX", "EBX", 5),
        ("RSP", "ESP", 19), ("RBP", "EBP", 4), ("RSI", "ESI", 13), ("RDI", "EDI", 14),
        ("R8", "", 9), ("R9", "", 8), ("R10", "", 7), ("R11", "", 6),
        ("R12", "", 3), ("R13", "", 2), ("R14", "", 1), ("R15", "", 0)
    ];

    private const int RipSlot = 16;

    [SupportedOSPlatform("linux")]
    private void RunLinux()
    {
        int pid = _process.Pid;
        bool targetGone = false;

        try
        {
            if (!SeizeAll(pid, out string error))
            {
                _startError = error;
                DetachAllLinux();
                _started.Set();
                return;
            }

            _running = true;
            _status = "Watching. Play the game and make the value change.";
            _started.Set();

            targetGone = LoopLinux(pid);
        }
        catch (Exception ex)
        {
            _status = "The watch stopped: " + ex.Message;
        }
        finally
        {
            if (!targetGone) DetachAllLinux();
            _tracees.Clear();
            _running = false;
            _started.Set();
        }
    }

    /// <summary>
    /// Seizes every thread and asks each to stop, so it can be armed. Threads started between
    /// listing and seizing are caught by going round again until the list stops growing.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private bool SeizeAll(int pid, out string error)
    {
        error = string.Empty;

        for (int round = 0; round < 20; round++)
        {
            bool grew = false;
            foreach (int tid in ProcFs.ThreadIds(pid))
            {
                if (_tracees.ContainsKey(tid)) continue;

                if (LibC.ptrace(Ptrace.Seize, tid, 0, Ptrace.OptionTraceClone) == -1)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == LibC.ESRCH) continue; // the thread ended in the meantime
                    if (_tracees.Count == 0)
                    {
                        error = errno == LibC.EPERM
                            ? LinuxAccess.DescribeRefusal(pid).Replace("read", "watch", StringComparison.Ordinal)
                            : $"Could not attach a debugger: error {errno}.";
                        return false;
                    }
                    continue;
                }

                _tracees[tid] = false;
                LibC.ptrace(Ptrace.Interrupt, tid, 0, 0);
                grew = true;
            }
            if (!grew) break;
        }

        if (_tracees.Count == 0)
        {
            error = "The game is no longer running.";
            return false;
        }
        return true;
    }

    /// <summary>Returns true when the target exited, meaning there is nothing left to detach from.</summary>
    [SupportedOSPlatform("linux")]
    private bool LoopLinux(int pid)
    {
        while (!_stop)
        {
            int tid = NextEvent(pid, out int status);
            if (tid == 0)
            {
                Thread.Sleep(1);
                continue;
            }

            if (HandleLinuxEvent(pid, tid, status)) return true;

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

    /// <summary>
    /// The next thread with something to report, or 0. Never waits on "any child": the app has
    /// children of its own that .NET is waiting for, and reaping one of those here would leave
    /// its owner waiting forever. So the next event is peeked at first and only taken when it is
    /// one of ours; if something else is in front, each tracee is asked in turn.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private unsafe int NextEvent(int pid, out int status)
    {
        status = 0;
        int st;

        byte* info = stackalloc byte[128];
        new Span<byte>(info, 128).Clear();
        if (LibC.waitid(LibC.P_ALL, 0, info, LibC.WEXITED | LibC.WSTOPPED | LibC.WNOWAIT | LibC.WNOHANG | LibC.WALL) == 0)
        {
            int who = *(int*)(info + 16); // siginfo_t.si_pid
            if (who == 0) return 0;

            // A thread the game started may report before its parent's clone event names it.
            if (!_tracees.ContainsKey(who) && Directory.Exists($"{ProcFs.Dir(pid)}/task/{who}"))
                _tracees[who] = false;

            if (_tracees.ContainsKey(who))
            {
                if (LibC.waitpid(who, &st, LibC.WNOHANG | LibC.WALL) == who)
                {
                    status = st;
                    return who;
                }
                return 0;
            }
        }

        foreach (int tid in _tracees.Keys)
        {
            if (LibC.waitpid(tid, &st, LibC.WNOHANG | LibC.WALL) == tid)
            {
                status = st;
                return tid;
            }
        }
        return 0;
    }

    /// <summary>Deals with one stop or exit. Returns true when the whole game has gone.</summary>
    [SupportedOSPlatform("linux")]
    private static unsafe int NewThreadId(int tid)
    {
        // PTRACE_GETEVENTMSG writes an unsigned long through its data pointer.
        ulong message = 0;
        return LibC.ptrace(Ptrace.GetEventMsg, tid, 0, (nint)(&message)) == 0 && message < int.MaxValue ? (int)message : 0;
    }

    [SupportedOSPlatform("linux")]
    private bool HandleLinuxEvent(int pid, int tid, int status)
    {
        if (LibC.Exited(status) || LibC.Signaled(status))
        {
            _tracees.Remove(tid);
            if (tid == pid || _tracees.Count == 0)
            {
                _status = "The game closed.";
                return true;
            }
            return false;
        }

        if (!LibC.Stopped(status)) return false;

        int signal = LibC.StopSignal(status);
        int ev = LibC.PtraceEvent(status);

        // Whatever stopped it, a thread is never let go again without its watchpoints.
        if (_tracees.TryGetValue(tid, out bool armed) && !armed) _tracees[tid] = ArmLinux(tid, arm: true);

        switch (ev)
        {
            case Ptrace.EventClone:
                if (NewThreadId(tid) is int child and > 0) _tracees.TryAdd(child, false);
                LibC.ptrace(Ptrace.Cont, tid, 0, 0);
                return false;

            case Ptrace.EventStop:
                // A job-control stop must stay stopped until the game is continued; anything else
                // here is our own interrupt or a new thread's first stop.
                if (signal is LibC.SIGSTOP or LibC.SIGTSTP or LibC.SIGTTIN or LibC.SIGTTOU)
                    LibC.ptrace(Ptrace.Listen, tid, 0, 0);
                else
                    LibC.ptrace(Ptrace.Cont, tid, 0, 0);
                return false;

            case 0:
                break;

            default:
                LibC.ptrace(Ptrace.Cont, tid, 0, 0);
                return false;
        }

        if (signal == LibC.SIGTRAP && TryRecordLinuxHit(tid))
        {
            LibC.ptrace(Ptrace.Cont, tid, 0, 0);
            return false;
        }

        // Not ours: the game's own signal goes back to it untouched.
        LibC.ptrace(Ptrace.Cont, tid, 0, signal);
        return false;
    }

    [SupportedOSPlatform("linux")]
    private unsafe bool TryRecordLinuxHit(int tid)
    {
        nint dr6 = PeekDebugRegister(tid, 6);
        if (dr6 == -1 && Marshal.GetLastPInvokeError() != 0) return false;

        int slot = DebugRegisters.FiredSlot((ulong)dr6);
        if (slot < 0 || slot >= _slots.Count) return false; // somebody else's trap

        // DR6 is sticky. Left set, it reports the same trap forever.
        PokeDebugRegister(tid, 6, 0);

        fixed (ulong* regs = _regs)
        {
            if (LibC.ptrace(Ptrace.GetRegs, tid, 0, (nint)regs) != 0) return true; // ours, but the registers are gone
        }

        bool wide = _process.Is64Bit;
        _registerBuffer.Clear();
        foreach (var (name64, name32, regSlot) in LinuxRegisters)
        {
            if (wide) _registerBuffer.Add((name64, _regs[regSlot]));
            else if (name32.Length > 0) _registerBuffer.Add((name32, _regs[regSlot] & 0xFFFF_FFFF));
        }

        ulong ip = wide ? _regs[RipSlot] : _regs[RipSlot] & 0xFFFF_FFFF;
        Record(ip, (uint)tid, _slots[slot].Address);
        return true;
    }

    /// <summary>Loads (or clears) the four watchpoints on one stopped thread.</summary>
    [SupportedOSPlatform("linux")]
    private bool ArmLinux(int tid, bool arm)
    {
        // DR7 off first: the kernel checks each address against the control word as it stands,
        // and a half-written set must never be live.
        if (!PokeDebugRegister(tid, 7, 0)) return false;
        for (int i = 0; i < DebugRegisters.SlotCount; i++)
            PokeDebugRegister(tid, i, arm && i < _slots.Count ? _slots[i].Address : 0);
        PokeDebugRegister(tid, 6, 0);
        return !arm || PokeDebugRegister(tid, 7, DebugRegisters.Control(_slots, _options.On));
    }

    /// <summary>
    /// Stops, disarms and releases every thread. A thread can only be detached while stopped, so
    /// each is interrupted and waited for; a real signal that was on its way to it is passed on,
    /// and a thread started at the last moment is released too rather than left stopped.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private unsafe void DetachAllLinux()
    {
        var pending = new Queue<int>(_tracees.Keys);
        foreach (int tid in pending) LibC.ptrace(Ptrace.Interrupt, tid, 0, 0);

        // A thread busy in the kernel (disk, a driver) can take longer than one wait to stop. It is
        // asked again rather than abandoned seized; only after three tries is it left, and then the
        // kernel releases it the moment Cheatmaster exits.
        var attempts = new Dictionary<int, int>();
        int abandoned = 0;

        while (pending.Count > 0)
        {
            int tid = pending.Dequeue();
            int pass = 0;
            bool stopped = false;
            var deadline = DateTime.UtcNow.AddSeconds(2);

            while (DateTime.UtcNow < deadline)
            {
                int st;
                int got = LibC.waitpid(tid, &st, LibC.WNOHANG | LibC.WALL);
                if (got == -1) break; // gone
                if (got == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }
                if (!LibC.Stopped(st)) break; // exited

                int signal = LibC.StopSignal(st);
                int ev = LibC.PtraceEvent(st);
                if (ev == Ptrace.EventClone && NewThreadId(tid) is int child and > 0 && !_tracees.ContainsKey(child))
                {
                    _tracees[child] = false;
                    pending.Enqueue(child);
                }

                if (ev == 0 && signal != LibC.SIGTRAP)
                {
                    // The game's own signal: hold it for the detach, and wait for our interrupt.
                    pass = signal;
                    LibC.ptrace(Ptrace.Cont, tid, 0, 0);
                    continue;
                }
                if (ev == Ptrace.EventClone)
                {
                    LibC.ptrace(Ptrace.Cont, tid, 0, 0);
                    continue;
                }

                stopped = true;
                break;
            }

            if (!stopped)
            {
                int tries = attempts.GetValueOrDefault(tid) + 1;
                attempts[tid] = tries;
                if (tries < 3 && Directory.Exists($"{ProcFs.Dir(_process.Pid)}/task/{tid}"))
                {
                    LibC.ptrace(Ptrace.Interrupt, tid, 0, 0);
                    pending.Enqueue(tid);
                }
                else if (tries >= 3)
                {
                    abandoned++;
                }
                continue;
            }
            ArmLinux(tid, arm: false);
            LibC.ptrace(Ptrace.Detach, tid, 0, pass);
        }

        _tracees.Clear();
        if (abandoned > 0)
            _status = $"{abandoned} game thread(s) would not stop to be released; they will be when Cheatmaster closes.";
    }

    private static nint PeekDebugRegister(int tid, int index)
    {
        Marshal.SetLastPInvokeError(0);
        return LibC.ptrace(Ptrace.PeekUser, tid, Ptrace.DebugRegisterOffset + index * 8, 0);
    }

    private static bool PokeDebugRegister(int tid, int index, ulong value) =>
        LibC.ptrace(Ptrace.PokeUser, tid, Ptrace.DebugRegisterOffset + index * 8, (nint)value) == 0;
}
