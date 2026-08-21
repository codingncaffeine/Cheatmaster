using System.Diagnostics;
using Cheatmaster.Core.Native;

namespace Cheatmaster.Core.Memory;

public sealed record ProcessCandidate(
    int Pid,
    string Name,
    string Title,
    string Path,
    bool HasWindow,
    bool LooksLikeGame,
    long WorkingSetBytes)
{
    public string Display => string.IsNullOrEmpty(Title) ? Name : $"{Name}  —  {Title}";
}

/// <summary>
/// Builds the attach list. Ranking matters more here than completeness: the process a user
/// wants is almost always a windowed, large, non-system process, so those float to the top.
/// </summary>
public static class ProcessList
{
    private static readonly string[] SystemNames =
    [
        "svchost", "csrss", "wininit", "winlogon", "services", "lsass", "smss", "dwm",
        "fontdrvhost", "spoolsv", "conhost", "sihost", "taskhostw", "ctfmon", "explorer",
        "runtimebroker", "searchhost", "shellexperiencehost", "textinputhost", "registry",
        "memory compression", "system", "idle", "audiodg", "wudfhost", "dllhost"
    ];

    public static List<ProcessCandidate> Enumerate(bool includeSystem = false)
    {
        Privileges.EnableDebugPrivilege();
        var list = new List<ProcessCandidate>();
        int self = Environment.ProcessId;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.Id <= 4) continue;

                string name = p.ProcessName;
                bool isSystem = IsSystemName(name);
                if (isSystem && !includeSystem) continue;

                string title = SafeTitle(p);
                long ws = SafeWorkingSet(p);
                string path = SafePath(p.Id);
                bool hasWindow = !string.IsNullOrEmpty(title);

                // A windowed process holding a few hundred MB is almost always the target.
                bool looksLikeGame = hasWindow && ws > 150L * 1024 * 1024 && !isSystem;

                list.Add(new ProcessCandidate(p.Id, name, title, path, hasWindow, looksLikeGame, ws));
            }
            catch
            {
                // Processes exit while being enumerated; skip them.
            }
            finally
            {
                p.Dispose();
            }
        }

        list.Sort(static (a, b) =>
        {
            if (a.LooksLikeGame != b.LooksLikeGame) return a.LooksLikeGame ? -1 : 1;
            if (a.HasWindow != b.HasWindow) return a.HasWindow ? -1 : 1;
            int ws = b.WorkingSetBytes.CompareTo(a.WorkingSetBytes);
            if (ws != 0) return ws;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return list;
    }

    private static bool IsSystemName(string name)
    {
        foreach (string s in SystemNames)
        {
            if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string SafeTitle(Process p)
    {
        try { return p.MainWindowTitle ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static long SafeWorkingSet(Process p)
    {
        try { return p.WorkingSet64; }
        catch { return 0; }
    }

    private static string SafePath(int pid)
    {
        nint h = Win32.OpenProcess(ProcessAccess.Minimal, false, (uint)pid);
        if (h == 0) return string.Empty;
        try
        {
            return QueryPath(h);
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }

    private static unsafe string QueryPath(nint handle)
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
}
