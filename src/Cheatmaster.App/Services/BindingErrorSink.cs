using Avalonia.Logging;

namespace Cheatmaster.App.Services;

/// <summary>
/// A failed binding is only a log line, so a typo in a binding path or a TwoWay binding aimed at a
/// read-only property just silently does nothing. This keeps those lines in a file, and hands them
/// to the app when started with --diag.
/// </summary>
public sealed class BindingErrorSink : ILogSink
{
    private readonly Action<string>? _report;
    private readonly string _logPath;
    private int _count;

    private BindingErrorSink(string logPath, Action<string>? report)
    {
        _logPath = logPath;
        _report = report;
    }

    public static void Attach(Action<string>? report = null)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Logger.Sink = new BindingErrorSink(Path.Combine(AppSettings.Directory, "binding-errors.log"), report);
    }

    public bool IsEnabled(LogEventLevel level, string area) =>
        level >= LogEventLevel.Warning && area == LogArea.Binding;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Write(messageTemplate);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        string line = messageTemplate;
        foreach (var value in propertyValues)
        {
            int open = line.IndexOf('{');
            int close = open < 0 ? -1 : line.IndexOf('}', open);
            if (close < 0) break;
            line = line[..open] + value + line[(close + 1)..];
        }
        Write(line);
    }

    private void Write(string line)
    {
        int count = Interlocked.Increment(ref _count);
        string stamped = DateTimeOffset.Now.ToString("O") + "  " + line + Environment.NewLine;

        // Bindings fail on the UI thread; the file write happens elsewhere.
        _ = Task.Run(() =>
        {
            lock (_logPath)
            {
                try
                {
                    File.AppendAllText(_logPath, stamped);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Diagnostics must never break the app.
                }
            }
        });

        // Only the first few are worth interrupting for; one bad binding can fire per row.
        if (count <= 3 && _report is { } report)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => report(line));
    }
}
