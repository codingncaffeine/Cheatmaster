using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cheatmaster.Core.Native;

namespace Cheatmaster.Core.Memory;

/// <summary>A Windows process, through an OpenProcess handle and the Win32 memory calls.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProcess : IProcessBackend
{
    private const int PageSize = 4096;

    private nint _handle;

    private WindowsProcess(nint handle) => _handle = handle;

    public static TargetProcess? Open(int pid, out string error)
    {
        error = string.Empty;
        Privileges.EnableDebugPrivilege();

        bool canWrite = true;
        nint handle = Win32.OpenProcess(ProcessAccess.ScanAndEdit, false, (uint)pid);
        if (handle == 0)
        {
            int err = Marshal.GetLastPInvokeError();
            handle = Win32.OpenProcess(ProcessAccess.ReadOnly, false, (uint)pid);
            canWrite = false;
            if (handle == 0)
            {
                error = DescribeOpenFailure(Marshal.GetLastPInvokeError() is 0 ? err : Marshal.GetLastPInvokeError());
                return null;
            }
        }

        string path = QueryImagePath(handle);
        string name = string.IsNullOrEmpty(path) ? $"pid {pid}" : Path.GetFileName(path);
        bool is64 = QueryIs64Bit(handle);

        return new TargetProcess(new WindowsProcess(handle), pid, name, path, is64, canWrite);
    }

    private static string DescribeOpenFailure(int err) => err switch
    {
        5 => Privileges.IsElevated
            ? "Access denied. The target is likely protected or running at a higher integrity level."
            : "Access denied. Restart Cheatmaster as administrator and try again.",
        87 => "The process is no longer running.",
        _ => $"OpenProcess failed with Win32 error {err}."
    };

    internal static unsafe string QueryImagePath(nint handle)
    {
        char[] buf = new char[1024];
        uint size = (uint)buf.Length;
        fixed (char* p = buf)
        {
            if (Win32.QueryFullProcessImageName(handle, 0, p, ref size) && size > 0)
                return new string(buf, 0, (int)size);
        }
        return string.Empty;
    }

    private static bool QueryIs64Bit(nint handle)
    {
        // IMAGE_FILE_MACHINE_UNKNOWN for processMachine means the process is running natively.
        if (Win32.IsWow64Process2(handle, out ushort processMachine, out _))
            return processMachine == 0;
        return Environment.Is64BitOperatingSystem;
    }

    public bool IsRunning => _handle != 0 && Win32.GetExitCodeProcess(_handle, out uint code) && code == 259; // STILL_ACTIVE

    public unsafe List<ModuleEntry> LoadModules()
    {
        var list = new List<ModuleEntry>();
        if (_handle == 0) return list;

        int capacity = 512;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            nint[] handles = new nint[capacity];
            uint needed;
            fixed (nint* p = handles)
            {
                if (!Win32.EnumProcessModulesEx(_handle, p, (uint)(handles.Length * sizeof(nint)), out needed, Win32.ListModulesAll))
                    return list;
            }

            int count = (int)(needed / sizeof(nint));
            if (count > capacity)
            {
                capacity = count + 64;
                continue;
            }

            char[] nameBuf = new char[512];
            for (int i = 0; i < count; i++)
            {
                string path;
                fixed (char* np = nameBuf)
                {
                    uint len = Win32.GetModuleFileNameEx(_handle, handles[i], np, (uint)nameBuf.Length);
                    path = len > 0 ? new string(nameBuf, 0, (int)len) : string.Empty;
                }

                if (!Win32.GetModuleInformation(_handle, handles[i], out ModuleInfo mi, (uint)sizeof(ModuleInfo)))
                    continue;

                list.Add(new ModuleEntry(
                    string.IsNullOrEmpty(path) ? "?" : Path.GetFileName(path),
                    path,
                    (ulong)mi.BaseOfDll,
                    mi.SizeOfImage));
            }
            break;
        }

        return list;
    }

    public void QueryRegions(ulong start, ulong end, IReadOnlyList<ModuleEntry> modules, List<MemoryRegion> into)
    {
        if (_handle == 0) return;

        ulong address = start;
        int mbiSize = Marshal.SizeOf<MemoryBasicInformation>();

        while (address < end)
        {
            if (Win32.VirtualQueryEx(_handle, (nuint)address, out MemoryBasicInformation mbi, (nuint)mbiSize) == 0)
                break;

            ulong size = mbi.RegionSize;
            if (size == 0) break;

            if (mbi.State == MemState.Commit)
                into.Add(new MemoryRegion(mbi.BaseAddress, size, mbi.Protect, mbi.Type));

            ulong next = mbi.BaseAddress + size;
            if (next <= address) break;
            address = next;
        }
    }

    public unsafe int Read(ulong address, Span<byte> buffer)
    {
        if (_handle == 0 || buffer.Length == 0) return 0;
        fixed (byte* p = buffer)
        {
            if (Win32.ReadProcessMemory(_handle, (nuint)address, p, (nuint)buffer.Length, out nuint read))
                return (int)read;
        }
        return 0;
    }

    public unsafe bool Write(ulong address, ReadOnlySpan<byte> data, bool liftProtection)
    {
        if (_handle == 0 || data.Length == 0) return false;

        fixed (byte* p = data)
        {
            if (Win32.WriteProcessMemory(_handle, (nuint)address, p, (nuint)data.Length, out nuint written) && (int)written == data.Length)
                return true;
        }

        return liftProtection && WriteWithProtectionLifted(address, data);
    }

    /// <summary>
    /// Lifts page protection, writes, and restores it.
    ///
    /// VirtualProtectEx reports the previous protection of the FIRST page only, so restoring
    /// that one value across a multi-page span would stamp it onto every page it touched — a
    /// write straddling a page boundary could strip execute rights from the following page and
    /// crash the target later. Each page is therefore lifted and restored on its own.
    /// </summary>
    private unsafe bool WriteWithProtectionLifted(ulong address, ReadOnlySpan<byte> data)
    {
        ulong end = address + (ulong)data.Length;
        var restore = new List<(ulong Base, nuint Size, uint Old)>(2);

        try
        {
            for (ulong page = address & ~(ulong)(PageSize - 1); page < end; page += PageSize)
            {
                ulong lo = Math.Max(page, address);
                ulong hi = Math.Min(page + PageSize, end);
                nuint size = (nuint)(hi - lo);

                if (!Win32.VirtualProtectEx(_handle, (nuint)lo, size, PageProtect.ExecuteReadWrite, out uint old))
                    return false;
                restore.Add((lo, size, old));
            }

            fixed (byte* p = data)
            {
                return Win32.WriteProcessMemory(_handle, (nuint)address, p, (nuint)data.Length, out nuint written)
                       && (int)written == data.Length;
            }
        }
        finally
        {
            foreach (var (pageBase, size, old) in restore)
                Win32.VirtualProtectEx(_handle, (nuint)pageBase, size, old, out _);
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            Win32.CloseHandle(_handle);
            _handle = 0;
        }
    }
}
