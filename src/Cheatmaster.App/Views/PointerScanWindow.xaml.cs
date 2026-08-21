using System.Windows;
using Cheatmaster.App.ViewModels;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.Views;

public partial class PointerScanWindow : Window
{
    private readonly PointerScanViewModel _viewModel;

    public PointerScanWindow(TargetProcess process, ulong target, string description, ScanType type, ulong expectedBits)
    {
        InitializeComponent();
        _viewModel = new PointerScanViewModel(process, target, description, type, expectedBits);
        DataContext = _viewModel;
    }

    /// <summary>The route the user settled on, or null if they closed without choosing.</summary>
    public PointerPath? Chosen { get; private set; }

    private void OnUse(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is null) return;
        Chosen = _viewModel.Selected.Path;
        DialogResult = true;
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
