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

    public CloudSyncWindow(AppSettings settings) : this() => DataContext = new CloudSyncViewModel(settings);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
