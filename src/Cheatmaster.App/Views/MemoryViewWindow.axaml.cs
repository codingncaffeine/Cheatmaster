using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cheatmaster.App.Services;
using Cheatmaster.App.ViewModels;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.Views;

public partial class MemoryViewWindow : Window
{
    private readonly MemoryViewModel? _viewModel;

    public MemoryViewWindow()
    {
        InitializeComponent();
        WindowChrome.DragBy(this, TitleBar);
        Closed += (_, _) => _viewModel?.Dispose();
    }

    public MemoryViewWindow(TargetProcess process, ulong address, string description) : this()
    {
        _viewModel = new MemoryViewModel(process, address, description);
        DataContext = _viewModel;
    }

    /// <summary>Raised when a field is worth keeping: its address and how to read it.</summary>
    public event Action<ulong, ScanType, string>? AddRequested;

    /// <summary>
    /// Selecting a byte. Five hundred cells with a click handler each would be five hundred more
    /// handlers than this needs, so the click is caught once on the way through.
    /// </summary>
    private void OnCellClicked(object? sender, PointerReleasedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is MemoryCell cell)
            _viewModel?.Select(cell);
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel is null) return;
        _viewModel.GoTo(AddressBox.Text ?? string.Empty);
        e.Handled = true;
    }

    private void OnAddField(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MemoryField field || _viewModel is null) return;

        AddRequested?.Invoke(_viewModel.SelectedAddress, field.Type,
            $"{_viewModel.Description} +{_viewModel.SelectedOffset:X} ({field.Label})");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
