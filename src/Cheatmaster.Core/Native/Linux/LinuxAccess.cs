using System.Diagnostics;

namespace Cheatmaster.Core.Native.Linux;

/// <summary>
/// The rule Linux applies before one program may read another's memory, and the one-click way
/// round it.
///
/// Most desktop distributions ship the Yama policy at 1: a program may only read or debug the
/// programs it started itself. A game started by Steam is not one of those, so every attach is
/// refused until the policy is relaxed. Setting it back to 0 — the kernel's classic rule, any
/// program of the same user — needs the administrator password once and lasts until the next
/// restart. It never reaches other users' programs.
/// </summary>
public static class LinuxAccess
{
    /// <summary>True when attaching to a game this user did not start from Cheatmaster will be refused.</summary>
    public static bool IsRestricted
    {
        get
        {
            if (!OperatingSystem.IsLinux()) return false;
            if (LibC.geteuid() == 0 || ProcFs.HasPtraceCapability()) return false;
            return ProcFs.PtraceScope() >= 1;
        }
    }

    /// <summary>Whether the policy can be relaxed without a restart. Level 3 is locked until boot.</summary>
    public static bool CanRelax => ProcFs.PtraceScope() is 1 or 2;

    /// <summary>The banner text, or empty when nothing stands in the way.</summary>
    public static string Hint => !IsRestricted ? string.Empty : ProcFs.PtraceScope() switch
    {
        3 => "Linux blocks reading game memory until the next restart (ptrace scope 3)",
        2 => "Linux only lets administrators read game memory (ptrace scope 2)",
        _ => "Linux limits reading game memory (ptrace scope 1)"
    };

    /// <summary>Why an attach was refused, worded for the person who has to fix it.</summary>
    public static string DescribeRefusal(int pid)
    {
        if (!OperatingSystem.IsLinux()) return "Access denied.";

        if (OwnerOf(pid) is { } owner && owner != LibC.geteuid() && LibC.geteuid() != 0)
            return "That program belongs to another user, so its memory cannot be read.";

        return ProcFs.PtraceScope() switch
        {
            3 => "Linux is set to refuse every memory reader until the next restart (ptrace scope 3). " +
                 "Change kernel.yama.ptrace_scope in /etc/sysctl.d and restart the computer.",
            2 => "Linux only lets administrators read another program's memory (ptrace scope 2). " +
                 "Use \"Allow memory access\" in the bar above, which asks for your password once.",
            1 => "Linux only lets a program read the memory of programs it started itself (ptrace scope 1). " +
                 "Use \"Allow memory access\" in the bar above, which asks for your password once and lasts until you restart.",
            _ => "Access denied. The program may be protected, or running as another user."
        };
    }

    private static uint? OwnerOf(int pid)
    {
        try
        {
            foreach (string line in File.ReadLines(ProcFs.Dir(pid) + "/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                string[] parts = line[4..].Split('\t', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 && uint.TryParse(parts[0], out uint uid) ? uid : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone.
        }
        return null;
    }

    /// <summary>
    /// Sets the policy to 0 until the next restart, through polkit's password prompt. Returns an
    /// empty string on success, or what went wrong.
    /// </summary>
    public static async Task<string> RelaxAsync()
    {
        string? sysctl = FindProgram("sysctl", "/usr/bin", "/usr/sbin", "/sbin", "/bin");
        string? pkexec = FindProgram("pkexec", "/usr/bin", "/bin");
        if (pkexec is null) return "pkexec is not installed, so there is no way to ask for the password. Run: sudo sysctl kernel.yama.ptrace_scope=0";
        if (sysctl is null) return "sysctl was not found. Run as root: echo 0 > /proc/sys/kernel/yama/ptrace_scope";

        try
        {
            var info = new ProcessStartInfo(pkexec)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            info.ArgumentList.Add(sysctl);
            info.ArgumentList.Add("-w");
            info.ArgumentList.Add("kernel.yama.ptrace_scope=0");

            using var process = Process.Start(info);
            if (process is null) return "pkexec could not be started.";

            string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode == 0 && ProcFs.PtraceScope() == 0) return string.Empty;
            return process.ExitCode switch
            {
                126 => "The password prompt was cancelled.",
                127 => "The request was refused: no authentication agent is running, or the password was wrong.",
                _ => stderr.Trim().Length > 0 ? stderr.Trim() : $"pkexec exited with code {process.ExitCode}."
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    private static string? FindProgram(string name, params string[] fallbacks)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(fallbacks))
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
