using System.Runtime.InteropServices;
using Cheatmaster.Core.Native;
using Cheatmaster.Core.Native.Linux;

namespace Cheatmaster.Core.Memory;

/// <summary>
/// A Linux process, read and written through process_vm_readv / process_vm_writev.
///
/// The kernel checks permission on every call rather than once at open, so there is no handle to
/// hold; the start time read at open stands in for one, so a pid handed to a new process after
/// the game exits is never mistaken for the game.
/// </summary>
internal sealed class LinuxProcess : IProcessBackend
{
    private readonly int _pid;
    private readonly ulong _startTime;
    private readonly bool _isWine;
    private readonly string _exeArgument;
    private int _memFd = -1;
    private bool _memFdTried;

    private LinuxProcess(int pid, ulong startTime, bool isWine, string exeArgument)
    {
        _pid = pid;
        _startTime = startTime;
        _isWine = isWine;
        _exeArgument = exeArgument;
    }

    public static TargetProcess? Open(int pid, out string error)
    {
        error = string.Empty;

        ulong startTime = ProcFs.ReadStartTime(pid);
        if (startTime == 0 || ProcFs.ReadState(pid) is '\0' or 'Z' or 'X')
        {
            error = "The process is no longer running.";
            return null;
        }

        var maps = ProcFs.ReadMaps(pid);
        if (maps.Count == 0)
        {
            error = LinuxAccess.DescribeRefusal(pid);
            return null;
        }

        // Permission is decided per call, so ask the question the scanner will ask.
        if (!ProbeRead(pid, maps, out int errno))
        {
            error = errno == LibC.ESRCH ? "The process is no longer running." : LinuxAccess.DescribeRefusal(pid);
            return null;
        }

        string[] cmdline = ProcFs.ReadCmdline(pid);
        bool isWine = Wine.IsWineProcess(cmdline, maps);
        string exeArgument = isWine && cmdline.Length > 0 && Wine.IsWindowsProgram(cmdline[0]) ? cmdline[0] : string.Empty;

        var backend = new LinuxProcess(pid, startTime, isWine, exeArgument);
        var modules = backend.LoadModules();

        string name;
        string path;
        bool is64;
        if (isWine && modules.Count > 0 && modules[0].Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var main = modules[0];
            name = main.Name;
            path = main.Path;
            is64 = PeImage.TryRead(backend.Read, main.Base, out var pe) ? pe.Is64Bit : true;
        }
        else
        {
            path = ProcFs.ReadExe(pid);
            name = path.Length > 0 ? Path.GetFileName(path) : ProcFs.ReadComm(pid);
            is64 = ElfIs64Bit(path);
        }
        if (string.IsNullOrEmpty(name)) name = $"pid {pid}";

        bool canWrite = backend.OpenMemFile();
        return new TargetProcess(backend, pid, name, path, is64, canWrite);
    }

    private static unsafe bool ProbeRead(int pid, List<MapLine> maps, out int errno)
    {
        errno = 0;
        foreach (var map in maps)
        {
            if (!map.Readable || map.Path.StartsWith("[v", StringComparison.Ordinal)) continue;

            ulong scratch = 0;
            var local = new IoVec { Base = (nint)(&scratch), Length = 1 };
            var remote = new IoVec { Base = (nint)map.Start, Length = 1 };
            if (LibC.process_vm_readv(pid, &local, 1, &remote, 1, 0) == 1) return true;

            errno = Marshal.GetLastPInvokeError();
            if (errno is LibC.EPERM or LibC.ESRCH) return false;
            // EFAULT: this one page is unreadable for its own reasons; try the next region.
        }
        return false;
    }

    private static bool ElfIs64Bit(string path)
    {
        if (string.IsNullOrEmpty(path)) return Environment.Is64BitOperatingSystem;
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> ident = stackalloc byte[5];
            if (file.Read(ident) == 5 && ident[0] == 0x7F && ident[1] == (byte)'E' && ident[2] == (byte)'L' && ident[3] == (byte)'F')
                return ident[4] == 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to the machine.
        }
        return Environment.Is64BitOperatingSystem;
    }

    public bool IsRunning =>
        ProcFs.ReadStartTime(_pid) == _startTime && ProcFs.ReadState(_pid) is not ('\0' or 'Z' or 'X');

    public unsafe int Read(ulong address, Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        fixed (byte* p = buffer)
        {
            var local = new IoVec { Base = (nint)p, Length = (nuint)buffer.Length };
            var remote = new IoVec { Base = (nint)address, Length = (nuint)buffer.Length };
            nint read = LibC.process_vm_readv(_pid, &local, 1, &remote, 1, 0);
            return read > 0 ? (int)read : 0;
        }
    }

    public unsafe bool Write(ulong address, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return false;
        fixed (byte* p = data)
        {
            var local = new IoVec { Base = (nint)p, Length = (nuint)data.Length };
            var remote = new IoVec { Base = (nint)address, Length = (nuint)data.Length };
            if (LibC.process_vm_writev(_pid, &local, 1, &remote, 1, 0) == data.Length) return true;

            // process_vm_writev honours page protection. Writing through /proc/<pid>/mem is the
            // kernel's own way past it — the same path a debugger uses to plant a breakpoint —
            // and it touches nothing but the bytes asked for.
            if (!OpenMemFile()) return false;
            return LibC.pwrite(_memFd, p, (nuint)data.Length, (long)address) == data.Length;
        }
    }

    private bool OpenMemFile()
    {
        if (_memFd >= 0) return true;
        if (_memFdTried) return false;
        _memFdTried = true;
        _memFd = LibC.open(ProcFs.Dir(_pid) + "/mem", LibC.O_RDWR | LibC.O_CLOEXEC);
        return _memFd >= 0;
    }

    public List<ModuleEntry> LoadModules()
    {
        var maps = ProcFs.ReadMaps(_pid);
        var modules = new List<ModuleEntry>();
        var byBase = new Dictionary<ulong, int>();
        Span<byte> magic = stackalloc byte[4];

        // Files mapped from offset 0 whose first bytes are an executable header: shared objects,
        // the executable itself, and any Windows module Wine was able to map straight from disk.
        for (int i = 0; i < maps.Count; i++)
        {
            var first = maps[i];
            if (!first.IsFile || first.Offset != 0) continue;
            if (byBase.ContainsKey(first.Start)) continue;

            ulong end = first.End;
            for (int j = i + 1; j < maps.Count && maps[j].Path == first.Path && maps[j].Start - end < 0x20_0000; j++)
                end = maps[j].End;

            if (Read(first.Start, magic) != 4) continue;
            bool elf = magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L' && magic[3] == (byte)'F';
            bool pe = magic[0] == (byte)'M' && magic[1] == (byte)'Z';
            if (!elf && !pe) continue;

            byBase[first.Start] = modules.Count;
            modules.Add(new ModuleEntry(Path.GetFileName(first.Path), first.Path, first.Start, (uint)Math.Min(end - first.Start, uint.MaxValue)));
        }

        if (_isWine) AddWindowsModules(maps, modules, byBase);

        modules.Sort((a, b) => a.Base.CompareTo(b.Base));
        int main = FindMainModule(modules);
        if (main > 0)
        {
            var entry = modules[main];
            modules.RemoveAt(main);
            modules.Insert(0, entry);
        }
        return modules;
    }

    /// <summary>
    /// Finds Windows modules by their headers. Wine places every image at an allocation-granular
    /// (64 KB) base with its header at the very start, which is the only place one is looked for.
    /// </summary>
    private void AddWindowsModules(List<MapLine> maps, List<ModuleEntry> modules, Dictionary<ulong, int> byBase)
    {
        ulong coveredUntil = 0;
        foreach (var map in maps)
        {
            if (!map.Readable || (map.Start & 0xFFFF) != 0 || map.Start < coveredUntil) continue;
            if (!PeImage.TryRead(Read, map.Start, out var pe)) continue;

            coveredUntil = map.Start + pe.SizeOfImage;
            string name = NameWindowsModule(maps, pe);
            string path = PathInside(maps, pe.Base, pe.Base + pe.SizeOfImage, name);
            if (path.Length == 0 && !pe.IsDll && _exeArgument.Length > 0)
                path = Wine.ToUnixPath(_pid, _exeArgument);

            var entry = new ModuleEntry(name, path, pe.Base, pe.SizeOfImage);
            if (byBase.TryGetValue(pe.Base, out int existing)) modules[existing] = entry;
            else
            {
                byBase[pe.Base] = modules.Count;
                modules.Add(entry);
            }
        }
    }

    private string NameWindowsModule(List<MapLine> maps, PeInfo pe)
    {
        // A mapping inside the image that names a Windows file is the most direct answer.
        string mapped = PathInside(maps, pe.Base, pe.Base + pe.SizeOfImage, null);
        if (mapped.Length > 0) return Path.GetFileName(mapped);

        if (!pe.IsDll && _exeArgument.Length > 0) return Wine.FileName(_exeArgument);
        if (pe.ExportName.Length > 0) return pe.ExportName;
        return $"module_{pe.Base:X}";
    }

    private static string PathInside(List<MapLine> maps, ulong start, ulong end, string? wantedName)
    {
        foreach (var map in maps)
        {
            if (map.End <= start || map.Start >= end || !map.IsFile) continue;
            string file = Path.GetFileName(map.Path);
            if (wantedName is not null && !string.Equals(file, wantedName, StringComparison.OrdinalIgnoreCase)) continue;
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".drv", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
                return map.Path;
        }
        return string.Empty;
    }

    private int FindMainModule(List<ModuleEntry> modules)
    {
        if (_isWine && _exeArgument.Length > 0)
        {
            string exe = Wine.FileName(_exeArgument);
            for (int i = 0; i < modules.Count; i++)
            {
                if (string.Equals(modules[i].Name, exe, StringComparison.OrdinalIgnoreCase)) return i;
            }
        }

        string self = ProcFs.ReadExe(_pid);
        for (int i = 0; i < modules.Count; i++)
        {
            if (modules[i].Path == self) return i;
        }
        return -1;
    }

    public void QueryRegions(ulong start, ulong end, IReadOnlyList<ModuleEntry> modules, List<MemoryRegion> into)
    {
        foreach (var map in ProcFs.ReadMaps(_pid))
        {
            if (map.End <= start || map.Start >= end) continue;
            // Kernel pseudo-pages: the clock page cannot be read, and vsyscall is not in the user range.
            if (map.Path.StartsWith("[vvar", StringComparison.Ordinal) || map.Path == "[vsyscall]") continue;

            into.Add(new MemoryRegion(map.Start, map.End - map.Start, Protection(map), Classify(map, modules)));
        }
    }

    private static uint Protection(in MapLine map)
    {
        if (!map.Readable) return map.Executable ? PageProtect.Execute : PageProtect.NoAccess;
        return (map.Writable, map.Executable) switch
        {
            (true, true) => PageProtect.ExecuteReadWrite,
            (true, false) => PageProtect.ReadWrite,
            (false, true) => PageProtect.ExecuteRead,
            _ => PageProtect.ReadOnly
        };
    }

    private static uint Classify(in MapLine map, IReadOnlyList<ModuleEntry> modules)
    {
        foreach (var module in modules)
        {
            if (map.Start >= module.Base && map.Start < module.End) return MemType.Image;
        }
        return map.IsFile || map.Shared ? MemType.Mapped : MemType.Private;
    }

    public void Dispose()
    {
        if (_memFd >= 0)
        {
            LibC.close(_memFd);
            _memFd = -1;
        }
    }
}
