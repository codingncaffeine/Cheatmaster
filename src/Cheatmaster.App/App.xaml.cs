using System.Windows;
using System.Windows.Threading;
using Cheatmaster.App.Services;
using Cheatmaster.App.ViewModels;
using Cheatmaster.Core.Native;

namespace Cheatmaster.App;

public partial class App : Application
{
    public static bool DiagnosticMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DiagnosticMode = e.Args.Any(a => string.Equals(a, "--diag", StringComparison.OrdinalIgnoreCase));

        // Ask for debug rights up front; without them most targets refuse to open.
        Privileges.EnableDebugPrivilege();

        DispatcherUnhandledException += OnUnhandledException;

        var window = new MainWindow();
        MainWindow = window;

        BindingErrorListener.Attach(DiagnosticMode
            ? line => window.ViewModel.Notify("Binding problem: " + line, NoticeKind.Error)
            : null);

        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message + Environment.NewLine + Environment.NewLine + e.Exception.StackTrace,
            "Cheatmaster hit an error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
