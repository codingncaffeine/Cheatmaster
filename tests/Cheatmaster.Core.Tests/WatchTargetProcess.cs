using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace Cheatmaster.Core.Tests;

/// <summary>
/// Starts the stand-in game and reports where the value it keeps changing lives.
///
/// The tests need a process that is not their own: Windows freezes an entire process while a
/// debug event is pending, so a test that attached to itself would stop the thread that has to
/// handle the event.
///
/// On Linux it must not be a child of the test either. .NET waits on its own children, and a
/// stopped child's ptrace stops would be taken by that wait instead of the watch. So it is
/// started through setsid -f, which leaves it parented to init; its pid comes back on stdout.
/// </summary>
internal sealed class WatchTargetProcess : IDisposable
{
    public const int FieldOffset = 0x18;

    private readonly Process _launcher;
    private readonly Process _process;

    private WatchTargetProcess(Process launcher, Process process, ulong block, int fieldOffset)
    {
        _launcher = launcher;
        _process = process;
        Block = block;
        Field = block + (ulong)fieldOffset;
    }

    /// <summary>The object the value belongs to — what the base register should turn out to hold.</summary>
    public ulong Block { get; }

    /// <summary>The address of the value itself.</summary>
    public ulong Field { get; }

    public int Pid => _process.Id;

    public bool HasExited => _process.HasExited;

    public static WatchTargetProcess Start(params string[] arguments)
    {
        string exe = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "Cheatmaster.WatchTarget.exe" : "Cheatmaster.WatchTarget");
        Assert.True(File.Exists(exe), $"the stand-in game was not built next to the tests: {exe}");

        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? exe : "setsid")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("-f");
            info.ArgumentList.Add(exe);
        }
        foreach (string argument in arguments) info.ArgumentList.Add(argument);

        var launcher = Process.Start(info);
        Assert.NotNull(launcher);

        string? blockLine = ReadLine(launcher!);
        string? offsetLine = ReadLine(launcher!);
        string? pidLine = ReadLine(launcher!);
        Assert.True(blockLine is not null && offsetLine is not null && pidLine is not null,
            "the stand-in game did not report its address");

        ulong block = ulong.Parse(blockLine!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int offset = int.Parse(offsetLine!, CultureInfo.InvariantCulture);
        int pid = int.Parse(pidLine!, CultureInfo.InvariantCulture);
        var process = OperatingSystem.IsWindows() ? launcher! : Process.GetProcessById(pid);
        return new WatchTargetProcess(launcher!, process, block, offset);
    }

    private static string? ReadLine(Process process)
    {
        var read = process.StandardOutput.ReadLineAsync();
        return read.Wait(TimeSpan.FromSeconds(15)) ? read.Result : null;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();
        if (!ReferenceEquals(_launcher, _process)) _launcher.Dispose();
    }
}
