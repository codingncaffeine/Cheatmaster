using System.Diagnostics;
using System.Runtime.InteropServices;
using Cheatmaster.Core.Native;

namespace Cheatmaster.Core.Memory;

public sealed record ModuleEntry(string Name, string Path, ulong Base, uint Size)
{
    public ulong End => Base + Size;
    public override string ToString() => $"{Name} @ {Base:X}";
}

/// <summary>A contiguous span of bytes inside a read buffer that came back intact.</summary>
public readonly record struct ValidRun(int Offset, int Length);

/// <summary>An opened handle to the process being edited, plus every read/write primitive built on it.</summary>
public sealed class TargetProcess : IDisposable
{
    private const int PageSize = 4096;

    private nint _handle;
    private List<ModuleEntry>? _modules;

    public int Pid { get; }
    public string Name { get; }
    public string ImagePath { get; }
    public bool Is64Bit { get; }
    public bool CanWrite { get; private set; }

    private TargetProcess(nint handle, int pid, string name, string imagePath, bool is64Bit, bool canWrite)
    {
        _handle = handle;
        Pid = pid;
        Name = name;
        ImagePath = imagePath;
        Is64Bit = is64Bit;
        CanWrite = canWrite;
    }

    public bool IsOpen => _handle != 0;

    public bool IsRunning
    {
        get
        {
            if (_handle == 0) return false;
            return Win32.GetExitCodeProcess(_handle, out uint code) && code == 259; // STILL_ACTIVE
        }
    }

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
        string name = string.IsNullOrEmpty(path) ? $"pid {pid}" : System.IO.Path.GetFileName(path);
        bool is64 = QueryIs64Bit(handle);

        return new TargetProcess(handle, pid, name, path, is64, canWrite);
    }

    private static string DescribeOpenFailure(int err) => err switch
    {
        5 => Privileges.IsElevated
            ? "Access denied. The target is likely protected or running at a higher integrity level."
            : "Access denied. Restart Cheatmaster as administrator and try again.",
        87 => "The process is no longer running.",
        _ => $"OpenProcess failed with Win32 error {err}."
    };

    private static unsafe string QueryImagePath(nint handle)
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

    public IReadOnlyList<ModuleEntry> Modules => _modules ??= LoadModules();

    public ModuleEntry? MainModule => Modules.Count > 0 ? Modules[0] : null;

    public void RefreshModules() => _modules = null;

    private unsafe List<ModuleEntry> LoadModules()
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
                    string.IsNullOrEmpty(path) ? "?" : System.IO.Path.GetFileName(path),
                    path,
                    (ulong)mi.BaseOfDll,
                    mi.SizeOfImage));
            }
            break;
        }

        return list;
    }

    public ModuleEntry? FindModule(string name)
    {
        foreach (var m in Modules)
        {
            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                return m;
        }
        return null;
    }

    /// <summary>Walks every committed region the filter accepts, merging adjacent compatible ones.</summary>
    public List<MemoryRegion> EnumerateRegions(RegionFilter filter)
    {
        var result = new List<MemoryRegion>();
        if (_handle == 0) return result;

        ulong address = filter.Start;
        int mbiSize = Marshal.SizeOf<MemoryBasicInformation>();

        while (address < filter.End)
        {
            if (Win32.VirtualQueryEx(_handle, (nuint)address, out MemoryBasicInformation mbi, (nuint)mbiSize) == 0)
                break;

            ulong size = mbi.RegionSize;
            if (size == 0) break;

            if (mbi.State == MemState.Commit)
            {
                var region = new MemoryRegion(mbi.BaseAddress, size, mbi.Protect, mbi.Type);
                if (filter.Matches(region))
                {
                    var clamped = filter.Clamp(region);
                    // Merge with the previous region when they touch, so a value straddling the
                    // boundary is still found.
                    if (result.Count > 0)
                    {
                        var prev = result[^1];
                        if (prev.End == clamped.Base)
                        {
                            result[^1] = prev with { Size = prev.Size + clamped.Size };
                            goto advance;
                        }
                    }
                    result.Add(clamped);
                }
            }

        advance:
            ulong next = mbi.BaseAddress + size;
            if (next <= address) break;
            address = next;
        }

        return result;
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

    public bool ReadExact(ulong address, Span<byte> buffer) => Read(address, buffer) == buffer.Length;

    public unsafe T ReadValue<T>(ulong address, T fallback = default) where T : unmanaged
    {
        T value = default;
        int size = sizeof(T);
        if (Win32.ReadProcessMemory(_handle, (nuint)address, (byte*)&value, (nuint)size, out nuint read) && (int)read == size)
            return value;
        return fallback;
    }

    /// <summary>
    /// Reads a block, and when the whole block cannot be read in one call, falls back to
    /// page-by-page reads so a single unreadable page does not throw away the rest.
    /// The runs describe which parts of the buffer actually hold target memory.
    /// </summary>
    public int ReadRuns(ulong address, Span<byte> buffer, List<ValidRun> runs)
    {
        runs.Clear();
        if (buffer.Length == 0) return 0;

        int read = Read(address, buffer);
        if (read == buffer.Length)
        {
            runs.Add(new ValidRun(0, buffer.Length));
            return buffer.Length;
        }

        int total = 0;
        int runStart = -1;
        int offset = 0;
        while (offset < buffer.Length)
        {
            // Step to the next page boundary first. Reading in fixed 4 KB strides from an
            // address that is not page-aligned lets one unreadable page take the readable
            // remainder of its predecessor down with it, which silently loses results near
            // the end of a region.
            int toBoundary = (int)(PageSize - ((address + (ulong)offset) & (PageSize - 1)));
            int len = Math.Min(toBoundary, buffer.Length - offset);

            if (Read(address + (ulong)offset, buffer.Slice(offset, len)) == len)
            {
                if (runStart < 0) runStart = offset;
                total += len;
            }
            else if (runStart >= 0)
            {
                runs.Add(new ValidRun(runStart, offset - runStart));
                runStart = -1;
            }

            offset += len;
        }
        if (runStart >= 0)
            runs.Add(new ValidRun(runStart, buffer.Length - runStart));

        return total;
    }

    public unsafe bool Write(ulong address, ReadOnlySpan<byte> data)
    {
        if (_handle == 0 || data.Length == 0) return false;

        fixed (byte* p = data)
        {
            if (Win32.WriteProcessMemory(_handle, (nuint)address, p, (nuint)data.Length, out nuint written) && (int)written == data.Length)
                return true;
        }

        return WriteWithProtectionLifted(address, data);
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

    public unsafe bool WriteValue<T>(ulong address, T value) where T : unmanaged
    {
        return Write(address, new ReadOnlySpan<byte>(&value, sizeof(T)));
    }

    /// <summary>Resolves a module-relative or absolute base plus a chain of offsets.</summary>
    public ulong ResolvePointerChain(ulong baseAddress, IReadOnlyList<int> offsets)
    {
        ulong address = baseAddress;
        for (int i = 0; i < offsets.Count; i++)
        {
            if (address == 0) return 0;
            ulong next = Is64Bit
                ? ReadValue<ulong>(address)
                : ReadValue<uint>(address);
            if (next == 0) return 0;
            address = next + (ulong)offsets[i];
            // A 32-bit target has no addresses past 4 GB; wrapping keeps a broken chain from
            // producing one the process could never hold.
            if (!Is64Bit) address &= 0xFFFF_FFFF;
        }
        return address;
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
