using System.Globalization;
using System.Text;

namespace Cheatmaster.Core.Native.Linux;

/// <summary>One line of /proc/&lt;pid&gt;/maps.</summary>
internal readonly record struct MapLine(ulong Start, ulong End, string Perms, ulong Offset, ulong Inode, string Path)
{
    public bool Readable => Perms.Length > 0 && Perms[0] == 'r';
    public bool Writable => Perms.Length > 1 && Perms[1] == 'w';
    public bool Executable => Perms.Length > 2 && Perms[2] == 'x';
    public bool Shared => Perms.Length > 3 && Perms[3] == 's';

    /// <summary>A real file, as opposed to an anonymous mapping or a kernel pseudo-region like [heap].</summary>
    public bool IsFile => Path.Length > 0 && Path[0] == '/' && Inode != 0;
}

/// <summary>Reads what the kernel publishes about a process under /proc.</summary>
internal static class ProcFs
{
    public static string Dir(int pid) => "/proc/" + pid.ToString(CultureInfo.InvariantCulture);

    public static List<MapLine> ReadMaps(int pid)
    {
        var lines = new List<MapLine>(1024);
        try
        {
            foreach (string line in File.ReadLines(Dir(pid) + "/maps"))
            {
                if (TryParseMapLine(line, out var map)) lines.Add(map);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The process exited, or is not ours to look at.
        }
        return lines;
    }

    internal static bool TryParseMapLine(string line, out MapLine map)
    {
        map = default;

        // start-end perms offset dev inode [path]; the path may itself contain spaces.
        int dash = line.IndexOf('-');
        int space1 = line.IndexOf(' ');
        if (dash <= 0 || space1 <= dash) return false;
        int space2 = line.IndexOf(' ', space1 + 1);
        int space3 = space2 < 0 ? -1 : line.IndexOf(' ', space2 + 1);
        int space4 = space3 < 0 ? -1 : line.IndexOf(' ', space3 + 1);
        if (space4 < 0) return false;
        int space5 = line.IndexOf(' ', space4 + 1);

        if (!ulong.TryParse(line.AsSpan(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong start) ||
            !ulong.TryParse(line.AsSpan(dash + 1, space1 - dash - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong end) ||
            !ulong.TryParse(line.AsSpan(space2 + 1, space3 - space2 - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong offset))
            return false;

        var inodeText = space5 < 0 ? line.AsSpan(space4 + 1) : line.AsSpan(space4 + 1, space5 - space4 - 1);
        ulong.TryParse(inodeText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong inode);

        string path = space5 < 0 ? string.Empty : line[(space5 + 1)..].Trim();
        // A file deleted while mapped keeps its old name with this suffix.
        if (path.EndsWith(" (deleted)", StringComparison.Ordinal)) path = path[..^10];

        map = new MapLine(start, end, line.Substring(space1 + 1, space2 - space1 - 1), offset, inode, path);
        return true;
    }

    /// <summary>The command line as the process sees it. Wine games carry their Windows path here.</summary>
    public static string[] ReadCmdline(int pid)
    {
        try
        {
            byte[] raw = File.ReadAllBytes(Dir(pid) + "/cmdline");
            if (raw.Length == 0) return [];
            return Encoding.UTF8.GetString(raw).TrimEnd('\0').Split('\0');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static string ReadComm(int pid)
    {
        try
        {
            return File.ReadAllText(Dir(pid) + "/comm").Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>The executable the process was started from, or empty when the kernel will not say.</summary>
    public static string ReadExe(int pid)
    {
        try
        {
            var target = new FileInfo(Dir(pid) + "/exe").LinkTarget;
            if (target is null) return string.Empty;
            return target.EndsWith(" (deleted)", StringComparison.Ordinal) ? target[..^10] : target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public static string ReadCwd(int pid)
    {
        try
        {
            return new DirectoryInfo(Dir(pid) + "/cwd").LinkTarget ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The fields of /proc/&lt;pid&gt;/stat after the command name. The name is in parentheses and
    /// can itself contain spaces and parentheses, so everything is read from the last ')'.
    /// </summary>
    private static string[]? ReadStatFields(int pid)
    {
        try
        {
            string stat = File.ReadAllText(Dir(pid) + "/stat");
            int close = stat.LastIndexOf(')');
            if (close < 0 || close + 2 >= stat.Length) return null;
            return stat[(close + 2)..].Split(' ');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// When the process started, in clock ticks since boot. Together with the pid it names one
    /// process for good: a pid alone can be handed to a new process once the old one exits.
    /// </summary>
    public static ulong ReadStartTime(int pid)
    {
        var fields = ReadStatFields(pid);
        // Field 22 of stat, which is index 19 once the pid and name are gone.
        return fields is { Length: > 19 } && ulong.TryParse(fields[19], CultureInfo.InvariantCulture, out ulong ticks) ? ticks : 0;
    }

    /// <summary>The one-letter run state: R, S, D, T, Z, ...; '\0' when the process is gone.</summary>
    public static char ReadState(int pid)
    {
        var fields = ReadStatFields(pid);
        return fields is { Length: > 0 } && fields[0].Length > 0 ? fields[0][0] : '\0';
    }

    public static long ReadResidentBytes(int pid)
    {
        try
        {
            string[] parts = File.ReadAllText(Dir(pid) + "/statm").Split(' ');
            return parts.Length > 1 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out long pages)
                ? pages * Environment.SystemPageSize
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>One variable from the process's start-up environment, or null.</summary>
    public static string? ReadEnvironment(int pid, string name)
    {
        try
        {
            byte[] raw = File.ReadAllBytes(Dir(pid) + "/environ");
            string prefix = name + "=";
            foreach (string entry in Encoding.UTF8.GetString(raw).Split('\0'))
            {
                if (entry.StartsWith(prefix, StringComparison.Ordinal)) return entry[prefix.Length..];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not ours to read.
        }
        return null;
    }

    public static List<int> ThreadIds(int pid)
    {
        var tids = new List<int>();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(Dir(pid) + "/task"))
            {
                if (int.TryParse(Path.GetFileName(dir), CultureInfo.InvariantCulture, out int tid)) tids.Add(tid);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone.
        }
        return tids;
    }

    public static IEnumerable<int> ProcessIds()
    {
        foreach (string dir in Directory.EnumerateDirectories("/proc"))
        {
            if (int.TryParse(Path.GetFileName(dir), CultureInfo.InvariantCulture, out int pid)) yield return pid;
        }
    }

    /// <summary>The Yama ptrace policy: 0 classic, 1 children only, 2 admin only, 3 nobody. -1 when Yama is absent.</summary>
    public static int PtraceScope()
    {
        try
        {
            string text = File.ReadAllText("/proc/sys/kernel/yama/ptrace_scope").Trim();
            return int.TryParse(text, CultureInfo.InvariantCulture, out int scope) ? scope : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>True when this process holds CAP_SYS_PTRACE, which lifts the Yama limit.</summary>
    public static bool HasPtraceCapability()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("CapEff:", StringComparison.Ordinal)) continue;
                return ulong.TryParse(line.AsSpan(7).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong caps)
                       && (caps & (1UL << 19)) != 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Assume not.
        }
        return false;
    }
}
