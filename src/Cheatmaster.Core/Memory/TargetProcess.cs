namespace Cheatmaster.Core.Memory;

public sealed record ModuleEntry(string Name, string Path, ulong Base, uint Size)
{
    public ulong End => Base + Size;
    public override string ToString() => $"{Name} @ {Base:X}";
}

/// <summary>A contiguous span of bytes inside a read buffer that came back intact.</summary>
public readonly record struct ValidRun(int Offset, int Length);

/// <summary>An opened process being edited, plus every read/write primitive built on it.</summary>
public sealed class TargetProcess : IDisposable
{
    private const int PageSize = 4096;

    private IProcessBackend? _backend;
    private List<ModuleEntry>? _modules;

    public int Pid { get; }
    public string Name { get; }
    public string ImagePath { get; }
    public bool Is64Bit { get; }
    public bool CanWrite { get; private set; }

    internal TargetProcess(IProcessBackend backend, int pid, string name, string imagePath, bool is64Bit, bool canWrite)
    {
        _backend = backend;
        Pid = pid;
        Name = name;
        ImagePath = imagePath;
        Is64Bit = is64Bit;
        CanWrite = canWrite;
    }

    public bool IsOpen => _backend is not null;

    public bool IsRunning => _backend?.IsRunning ?? false;

    public static TargetProcess? Open(int pid, out string error)
    {
        if (OperatingSystem.IsWindows()) return WindowsProcess.Open(pid, out error);
        if (OperatingSystem.IsLinux()) return LinuxProcess.Open(pid, out error);

        error = "Attaching to a process is not supported on this system.";
        return null;
    }

    public IReadOnlyList<ModuleEntry> Modules => _modules ??= _backend?.LoadModules() ?? [];

    public ModuleEntry? MainModule => Modules.Count > 0 ? Modules[0] : null;

    public void RefreshModules() => _modules = null;

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
        if (_backend is null) return result;

        var raw = new List<MemoryRegion>();
        _backend.QueryRegions(filter.Start, filter.End, Modules, raw);

        foreach (var region in raw)
        {
            if (!filter.Matches(region)) continue;

            var clamped = filter.Clamp(region);
            // Merge with the previous region when they touch, so a value straddling the
            // boundary is still found.
            if (result.Count > 0 && result[^1].End == clamped.Base)
            {
                var prev = result[^1];
                result[^1] = prev with { Size = prev.Size + clamped.Size };
                continue;
            }
            result.Add(clamped);
        }

        return result;
    }

    public int Read(ulong address, Span<byte> buffer) =>
        _backend is null || buffer.Length == 0 ? 0 : _backend.Read(address, buffer);

    public bool ReadExact(ulong address, Span<byte> buffer) => Read(address, buffer) == buffer.Length;

    public unsafe T ReadValue<T>(ulong address, T fallback = default) where T : unmanaged
    {
        T value = default;
        return Read(address, new Span<byte>(&value, sizeof(T))) == sizeof(T) ? value : fallback;
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

    public bool Write(ulong address, ReadOnlySpan<byte> data) =>
        _backend is not null && data.Length > 0 && _backend.Write(address, data);

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
        _backend?.Dispose();
        _backend = null;
    }
}
