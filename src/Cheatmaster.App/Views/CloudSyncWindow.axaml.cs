using Avalonia.Controls;
using Avalonia.Interactivity;
using Cheatmaster.App.Services;
using Cheatmaster.App.ViewModels;

namespace Cheatmaster.App.Views;

public partial class CloudSyncWindow : Window
{
    public CloudSyncWindow()
    {
        InitializeComponent();
        WindowChrome.DragBy(this, TitleBar);
    }

    /// <summary>
    /// Reading the saved sign-in can wait on the desktop keyring, which may be locked and asking for a
    /// password, so the view model is built off the UI thread once the window is up.
    /// </summary>
    public CloudSyncWindow(AppSettings settings) : this()
    {
        Opened += async (_, _) =>
        {
            if (DataContext is not null) return;
            DataContext = await Task.Run(() => new CloudSyncViewModel(settings));
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
