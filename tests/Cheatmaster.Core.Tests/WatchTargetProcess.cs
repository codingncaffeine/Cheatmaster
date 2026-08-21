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
/// </summary>
internal sealed class WatchTargetProcess : IDisposable
{
    public const int FieldOffset = 0x18;

    private readonly Process _process;

    private WatchTargetProcess(Process process, ulong block, int fieldOffset)
    {
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

    public static WatchTargetProcess Start()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Cheatmaster.WatchTarget.exe");
        Assert.True(File.Exists(exe), $"the stand-in game was not built next to the tests: {exe}");

        var process = Process.Start(new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);

        string? blockLine = ReadLine(process!);
        string? offsetLine = ReadLine(process!);
        Assert.True(blockLine is not null && offsetLine is not null, "the stand-in game did not report its address");

        ulong block = ulong.Parse(blockLine!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int offset = int.Parse(offsetLine!, CultureInfo.InvariantCulture);
        return new WatchTargetProcess(process!, block, offset);
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
    }
}
