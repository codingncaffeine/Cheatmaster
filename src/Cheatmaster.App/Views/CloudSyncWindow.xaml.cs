using System.Windows;
using Cheatmaster.App.Services;
using Cheatmaster.App.ViewModels;

namespace Cheatmaster.App.Views;

public partial class CloudSyncWindow : Window
{
    public CloudSyncWindow(AppSettings settings)
    {
        InitializeComponent();
        DataContext = new CloudSyncViewModel(settings);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
