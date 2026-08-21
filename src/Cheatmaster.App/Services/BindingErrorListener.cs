using System.Diagnostics;
using System.IO;
using System.Text;

namespace Cheatmaster.App.Services;

/// <summary>
/// WPF reports a failed binding by writing a line to a trace source nobody reads, so a typo in a
/// binding path or a TwoWay binding aimed at a read-only property just silently does nothing.
/// This captures those lines to a log file, and hands them to the app when started with --diag.
/// </summary>
public sealed class BindingErrorListener : TraceListener
{
    private readonly StringBuilder _pending = new();
    private readonly Action<string>? _report;
    private readonly string _logPath;
    private int _count;

    private BindingErrorListener(string logPath, Action<string>? report)
    {
        _logPath = logPath;
        _report = report;
    }

    public static BindingErrorListener Attach(Action<string>? report = null)
    {
        string directory = AppSettings.Directory;
        Directory.CreateDirectory(directory);

        var listener = new BindingErrorListener(Path.Combine(directory, "binding-errors.log"), report);

        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Error | SourceLevels.Warning;
        return listener;
    }

    public int Count => _count;

    public override void Write(string? message)
    {
        if (message is not null) _pending.Append(message);
    }

    public override void WriteLine(string? message)
    {
        _pending.Append(message);
        string line = _pending.ToString().Trim();
        _pending.Clear();
        if (line.Length == 0) return;

        _count++;
        try
        {
            File.AppendAllText(_logPath, DateTimeOffset.Now.ToString("O") + "  " + line + Environment.NewLine);
        }
        catch
        {
            // Diagnostics must never break the app.
        }

        // Only the first few are worth interrupting for; one bad binding can fire per row.
        if (_count <= 3) _report?.Invoke(line);
    }
}
