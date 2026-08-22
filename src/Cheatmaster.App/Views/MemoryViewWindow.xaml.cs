using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cheatmaster.App.ViewModels;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.Views;

public partial class MemoryViewWindow : Window
{
    private readonly MemoryViewModel _viewModel;

    public MemoryViewWindow(TargetProcess process, ulong address, string description)
    {
        InitializeComponent();
        _viewModel = new MemoryViewModel(process, address, description);
        DataContext = _viewModel;
    }

    /// <summary>Raised when a field is worth keeping: its address and how to read it.</summary>
    public event Action<ulong, ScanType, string>? AddRequested;

    /// <summary>
    /// Selecting a byte. Five hundred cells with a click handler each would be five hundred more
    /// elements than this needs, so the click is caught once on the way through.
    /// </summary>
    private void OnCellClicked(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is MemoryCell cell)
            _viewModel.Select(cell);
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _viewModel.GoTo(AddressBox.Text);
        e.Handled = true;
    }

    private void OnAddField(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MemoryField field) return;

        AddRequested?.Invoke(_viewModel.SelectedAddress, field.Type,
            $"{_viewModel.Description} +{_viewModel.SelectedOffset:X} ({field.Label})");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e) => _viewModel.Dispose();
}
