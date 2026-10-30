using Avalonia.Controls;
using Avalonia.Interactivity;
using Cheatmaster.App.Services;
using Cheatmaster.App.ViewModels;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.Views;

public partial class PointerScanWindow : Window
{
    private readonly PointerScanViewModel? _viewModel;

    public PointerScanWindow()
    {
        InitializeComponent();
        WindowChrome.DragBy(this, TitleBar);
    }

    public PointerScanWindow(TargetProcess process, ulong target, string description, ScanType type,
        ulong expectedBits, int? finalOffset = null) : this()
    {
        _viewModel = new PointerScanViewModel(process, target, description, type, expectedBits, finalOffset);
        DataContext = _viewModel;
    }

    /// <summary>Shows the window; completes with the route the user settled on, or null.</summary>
    public Task<PointerPath?> ChooseAsync(Window owner) => ShowDialog<PointerPath?>(owner);

    private void OnUse(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.Selected is null) return;
        Close(_viewModel.Selected.Path);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
