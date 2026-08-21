using System.Runtime.InteropServices;

namespace Cheatmaster.Core.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryBasicInformation
{
    public ulong BaseAddress;
    public ulong AllocationBase;
    public uint AllocationProtect;
    public uint Alignment1;
    public ulong RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
    public uint Alignment2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ModuleInfo
{
    public nint BaseOfDll;
    public uint SizeOfImage;
    public nint EntryPoint;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TokenPrivileges
{
    public uint PrivilegeCount;
    public long Luid;
    public uint Attributes;
}

internal static class ProcessAccess
{
    public const uint Terminate = 0x0001;
    public const uint VmOperation = 0x0008;
    public const uint VmRead = 0x0010;
    public const uint VmWrite = 0x0020;
    public const uint QueryInformation = 0x0400;
    public const uint QueryLimitedInformation = 0x1000;
    public const uint Synchronize = 0x00100000;

    /// <summary>Full rights needed to scan and edit a target.</summary>
    public const uint ScanAndEdit = VmOperation | VmRead | VmWrite | QueryInformation | Synchronize;

    /// <summary>Fallback when a target refuses write access.</summary>
    public const uint ReadOnly = VmRead | QueryInformation | Synchronize;

    /// <summary>Enough to read a name and path for the process list.</summary>
    public const uint Minimal = QueryLimitedInformation;
}

internal static class MemState
{
    public const uint Commit = 0x1000;
    public const uint Reserve = 0x2000;
    public const uint Free = 0x10000;
}

internal static class MemType
{
    public const uint Private = 0x20000;
    public const uint Mapped = 0x40000;
    public const uint Image = 0x1000000;
}

internal static class PageProtect
{
    public const uint NoAccess = 0x01;
    public const uint ReadOnly = 0x02;
    public const uint ReadWrite = 0x04;
    public const uint WriteCopy = 0x08;
    public const uint Execute = 0x10;
    public const uint ExecuteRead = 0x20;
    public const uint ExecuteReadWrite = 0x40;
    public const uint ExecuteWriteCopy = 0x80;
    public const uint Guard = 0x100;
    public const uint NoCache = 0x200;
    public const uint WriteCombine = 0x400;

    public const uint AccessMask = 0xFF;
    public const uint WritableMask = ReadWrite | WriteCopy | ExecuteReadWrite | ExecuteWriteCopy;
    public const uint ExecutableMask = Execute | ExecuteRead | ExecuteReadWrite | ExecuteWriteCopy;
}

internal static partial class Win32
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadProcessMemory(nint process, nuint address, byte* buffer, nuint size, out nuint bytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WriteProcessMemory(nint process, nuint address, byte* buffer, nuint size, out nuint bytesWritten);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint VirtualQueryEx(nint process, nuint address, out MemoryBasicInformation info, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualProtectEx(nint process, nuint address, nuint size, uint newProtect, out uint oldProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, nint previousState, nint returnLength);

    [LibraryImport("psapi.dll", EntryPoint = "EnumProcessModulesEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool EnumProcessModulesEx(nint process, nint* modules, uint cb, out uint needed, uint filterFlag);

    [LibraryImport("psapi.dll", EntryPoint = "GetModuleFileNameExW", SetLastError = true)]
    internal static unsafe partial uint GetModuleFileNameEx(nint process, nint module, char* buffer, uint size);

    [LibraryImport("psapi.dll", EntryPoint = "GetModuleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetModuleInformation(nint process, nint module, out ModuleInfo info, uint cb);

    internal const uint ListModulesAll = 0x03;
    internal const uint TokenAdjustPrivileges = 0x0020;
    internal const uint TokenQuery = 0x0008;
    internal const uint SePrivilegeEnabled = 0x0002;
}
