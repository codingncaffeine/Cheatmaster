using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cheatmaster.App.ViewModels;

namespace Cheatmaster.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private void OnGameDoubleClick(object? sender, TappedEventArgs e)
    {
        if (DataContext is LibraryViewModel library && library.Selected is LibraryGameRow row)
            library.Open(row);
    }

    private void OnCloudBackup(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow main) main.ShowCloudBackup();
    }
}
