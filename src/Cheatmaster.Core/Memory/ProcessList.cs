using System.Diagnostics;
using System.Runtime.Versioning;
using Cheatmaster.Core.Native;
using Cheatmaster.Core.Native.Linux;

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
        "memory compression", "system", "idle", "audiodg", "wudfhost", "dllhost",
        // Wine's own services, which every Wine prefix runs alongside the game.
        "winedevice", "plugplay", "rpcss", "tabtip", "start", "winedbg", "steam",
        // The Linux desktop itself: never what anyone wants to attach to.
        "systemd", "dbus-daemon", "dbus-broker", "pipewire", "pipewire-pulse", "wireplumber", "pulseaudio",
        "xwayland", "xorg", "kwin_wayland", "kwin_x11", "plasmashell", "ksmserver", "kded6", "kded5",
        "gnome-shell", "mutter", "xdg-desktop-portal", "xdg-desktop-portal-kde", "xdg-desktop-portal-gnome",
        "xdg-desktop-portal-gtk", "wineserver", "steamwebhelper", "gamescope", "at-spi-bus-launcher",
        "at-spi2-registryd", "gvfsd", "baloo_file", "kwalletd6", "kwalletd5", "gnome-keyring-daemon", "polkit-kde-authentication-agent-1"
    ];

    public static List<ProcessCandidate> Enumerate(bool includeSystem = false)
    {
        var list = OperatingSystem.IsWindows() ? EnumerateWindows(includeSystem) : EnumerateLinux(includeSystem);

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

    [SupportedOSPlatform("windows")]
    private static List<ProcessCandidate> EnumerateWindows(bool includeSystem)
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

        return list;
    }

    /// <summary>
    /// Linux has no window titles per process, so they come from X11 where there is one, and a
    /// Wine program is named by the Windows executable it is running rather than by "wine64".
    /// </summary>
    private static List<ProcessCandidate> EnumerateLinux(bool includeSystem)
    {
        var list = new List<ProcessCandidate>();
        int self = Environment.ProcessId;
        var titles = X11Windows.TitlesByPid();

        foreach (int pid in ProcFs.ProcessIds())
        {
            if (pid == self) continue;

            string[] cmdline = ProcFs.ReadCmdline(pid);
            if (cmdline.Length == 0) continue; // kernel threads, and processes already gone

            bool isWine = Wine.IsWindowsProgram(cmdline[0]);
            string name = isWine ? Wine.FileName(cmdline[0]) : ProcFs.ReadComm(pid);
            if (name.Length == 0) continue;

            string stem = isWine ? Path.GetFileNameWithoutExtension(name) : name;
            bool isSystem = IsSystemName(stem);
            if (isSystem && !includeSystem) continue;

            string path = isWine ? Wine.ToUnixPath(pid, cmdline[0]) : ProcFs.ReadExe(pid);
            if (isWine && path.Length == 0) path = cmdline[0];
            if (!includeSystem && path.Length == 0 && !isWine) continue; // not ours to look at

            titles.TryGetValue(pid, out string? title);
            long rss = ProcFs.ReadResidentBytes(pid);
            bool hasWindow = !string.IsNullOrEmpty(title);

            // Native Wayland windows cannot be seen from here, so a big Windows program under
            // Wine counts as a likely game even without a title.
            bool looksLikeGame = !isSystem && rss > 150L * 1024 * 1024 && (hasWindow || isWine);

            list.Add(new ProcessCandidate(pid, name, title ?? string.Empty, path, hasWindow, looksLikeGame, rss));
        }

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

    [SupportedOSPlatform("windows")]
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

    [SupportedOSPlatform("windows")]
    private static string QueryPath(nint handle) => Memory.WindowsProcess.QueryImagePath(handle);
}
