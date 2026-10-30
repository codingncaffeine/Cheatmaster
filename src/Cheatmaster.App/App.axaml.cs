using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Cheatmaster.App.Services;
using Cheatmaster.App.ViewModels;
using Cheatmaster.Core.Native;

namespace Cheatmaster.App;

public partial class App : Application
{
    public static bool DiagnosticMode { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DiagnosticMode = desktop.Args?.Any(a => string.Equals(a, "--diag", StringComparison.OrdinalIgnoreCase)) == true;

            // Ask for debug rights up front; without them most Windows targets refuse to open.
            Privileges.EnableDebugPrivilege();

            Dispatcher.UIThread.UnhandledException += OnUnhandledException;

            var window = new MainWindow();
            desktop.MainWindow = window;

            BindingErrorSink.Attach(DiagnosticMode
                ? line => window.ViewModel.Notify("Binding problem: " + line, NoticeKind.Error)
                : null);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _ = Dialogs.ShowErrorAsync("Cheatmaster hit an error",
            e.Exception.Message + Environment.NewLine + Environment.NewLine + e.Exception.StackTrace);
    }
}
