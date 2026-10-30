using System.Runtime.InteropServices;

namespace Cheatmaster.Core.Native.Linux;

[StructLayout(LayoutKind.Sequential)]
internal struct IoVec
{
    public nint Base;
    public nuint Length;
}

/// <summary>The C library calls the Linux backend is built on: process memory, ptrace and waiting.</summary>
internal static partial class LibC
{
    private const string Library = "libc";

    public const int EPERM = 1;
    public const int ESRCH = 3;
    public const int EINTR = 4;
    public const int EFAULT = 14;
    public const int ECHILD = 10;

    public const int O_RDONLY = 0;
    public const int O_RDWR = 2;
    public const int O_CLOEXEC = 0x80000;

    public const int SIGTRAP = 5;
    public const int SIGSTOP = 19;
    public const int SIGTSTP = 20;
    public const int SIGTTIN = 21;
    public const int SIGTTOU = 22;

    public const int WNOHANG = 1;
    public const int WSTOPPED = 2;
    public const int WEXITED = 4;
    public const int WNOWAIT = 0x01000000;
    public const int WALL = 0x40000000;
    public const int P_ALL = 0;

    [LibraryImport(Library, SetLastError = true)]
    internal static unsafe partial nint process_vm_readv(int pid, IoVec* local, nuint localCount, IoVec* remote, nuint remoteCount, nuint flags);

    [LibraryImport(Library, SetLastError = true)]
    internal static unsafe partial nint process_vm_writev(int pid, IoVec* local, nuint localCount, IoVec* remote, nuint remoteCount, nuint flags);

    [LibraryImport(Library, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int open(string path, int flags);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int close(int fd);

    [LibraryImport(Library, SetLastError = true)]
    internal static unsafe partial nint pwrite(int fd, byte* buffer, nuint count, long offset);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int kill(int pid, int signal);

    [LibraryImport(Library)]
    internal static partial uint geteuid();

    [LibraryImport(Library, SetLastError = true)]
    internal static partial nint ptrace(nint request, int pid, nint address, nint data);

    [LibraryImport(Library, SetLastError = true)]
    internal static unsafe partial int waitpid(int pid, int* status, int options);

    [LibraryImport(Library, SetLastError = true)]
    internal static unsafe partial int waitid(int idType, int id, byte* info, int options);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int syscall(nint number, int a, int b);

    /// <summary>The kernel's thread id for the calling thread, which ptrace needs and .NET does not expose.</summary>
    internal static int GetThreadId() => syscall(186 /* SYS_gettid */, 0, 0);

    // Wait status decoding, as the C macros spell it.
    internal static bool Exited(int status) => (status & 0x7F) == 0;
    internal static bool Signaled(int status) => (status & 0x7F) != 0 && (status & 0x7F) != 0x7F;
    internal static bool Stopped(int status) => (status & 0xFF) == 0x7F;
    internal static int StopSignal(int status) => (status >> 8) & 0xFF;
    internal static int PtraceEvent(int status) => (status >> 16) & 0xFF;
}

internal static class Ptrace
{
    public const nint PeekUser = 3;
    public const nint PokeUser = 6;
    public const nint Cont = 7;
    public const nint GetRegs = 12;
    public const nint Detach = 17;
    public const nint GetEventMsg = 0x4201;
    public const nint Seize = 0x4206;
    public const nint Interrupt = 0x4207;
    public const nint Listen = 0x4208;

    public const nint OptionTraceClone = 0x08;

    public const int EventClone = 3;
    public const int EventExec = 4;
    public const int EventExit = 6;
    public const int EventStop = 128;

    /// <summary>offsetof(struct user, u_debugreg) on x86-64.</summary>
    public const int DebugRegisterOffset = 848;

    /// <summary>sizeof(struct user_regs_struct) on x86-64: 27 eight-byte slots.</summary>
    public const int RegisterBlockSize = 27 * 8;
}
