using System.Runtime.InteropServices;

namespace Cheatmaster.Core.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct ThreadEntry32
{
    public uint Size;
    public uint Usage;
    public uint ThreadId;
    public uint OwnerProcessId;
    public int BasePri;
    public int DeltaPri;
    public uint Flags;
}

internal static class DebugEventCode
{
    public const uint Exception = 1;
    public const uint CreateThread = 2;
    public const uint CreateProcess = 3;
    public const uint ExitThread = 4;
    public const uint ExitProcess = 5;
    public const uint LoadDll = 6;
    public const uint UnloadDll = 7;
    public const uint OutputDebugString = 8;
    public const uint Rip = 9;
}

internal static class DebugStatus
{
    public const uint Continue = 0x00010002;
    public const uint ExceptionNotHandled = 0x80010001;
}

internal static class ExceptionCode
{
    public const uint Breakpoint = 0x80000003;

    /// <summary>What a hardware data breakpoint reports — not a breakpoint exception.</summary>
    public const uint SingleStep = 0x80000004;
}

internal static class ThreadAccess
{
    public const uint SuspendResume = 0x0002;
    public const uint GetContext = 0x0008;
    public const uint SetContext = 0x0010;
    public const uint QueryInformation = 0x0040;

    public const uint ForBreakpoints = SuspendResume | GetContext | SetContext | QueryInformation;
}

/// <summary>
/// Which parts of a thread's register state a Get/SetThreadContext call touches. The machine
/// bit differs between the native x64 layout and the 32-bit layout a WoW64 target uses, so the
/// two flavours never share a value.
/// </summary>
internal static class ContextFlags
{
    private const uint Amd64 = 0x00100000;
    private const uint I386 = 0x00010000;

    public const uint Amd64Control = Amd64 | 0x01;
    public const uint Amd64Integer = Amd64 | 0x02;
    public const uint Amd64DebugRegisters = Amd64 | 0x10;
    public const uint Amd64Full = Amd64Control | Amd64Integer | Amd64DebugRegisters;

    public const uint I386Control = I386 | 0x01;
    public const uint I386Integer = I386 | 0x02;
    public const uint I386DebugRegisters = I386 | 0x10;
    public const uint I386Full = I386Control | I386Integer | I386DebugRegisters;
}

/// <summary>The subset of the Win32 debugging API needed to watch an address for access.</summary>
internal static partial class DebugApi
{
    internal const uint SnapThread = 0x00000004;
    internal const int WaitTimedOut = 121; // ERROR_SEM_TIMEOUT

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DebugActiveProcess(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DebugActiveProcessStop(uint processId);

    /// <summary>
    /// Without this, the target dies the moment we detach or crash — the default is that a
    /// debuggee goes down with its debugger. Call it immediately after attaching.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DebugSetProcessKillOnExit([MarshalAs(UnmanagedType.Bool)] bool killOnExit);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WaitForDebugEventEx(byte* debugEvent, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ContinueDebugEvent(uint processId, uint threadId, uint continueStatus);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint SuspendThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetThreadContext(nint thread, byte* context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetThreadContext(nint thread, byte* context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool Wow64GetThreadContext(nint thread, byte* context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool Wow64SetThreadContext(nint thread, byte* context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Thread32First(nint snapshot, ref ThreadEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Thread32Next(nint snapshot, ref ThreadEntry32 entry);
}
