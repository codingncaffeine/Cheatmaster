using System.Windows;
using Cheatmaster.App.ViewModels;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.Views;

public partial class AccessWatchWindow : Window
{
    private readonly AccessWatchViewModel _viewModel;
    private readonly TargetProcess _process;
    private readonly ulong _expectedBits;

    public AccessWatchWindow(TargetProcess process, ulong address, string description, ScanType type, ulong expectedBits)
    {
        InitializeComponent();
        _process = process;
        _expectedBits = expectedBits;
        _viewModel = new AccessWatchViewModel(process, address, description, type);
        DataContext = _viewModel;
    }

    /// <summary>The route the user settled on, or null if they closed without tracing one.</summary>
    public PointerPath? Chosen { get; private set; }

    /// <summary>A field picked out of the object, on its way to the cheat table.</summary>
    public event Action<ulong, ScanType, string>? AddRequested;

    /// <summary>
    /// Hands the offset the instruction just revealed to the pointer search. Knowing where the
    /// value sits inside its object is what turns that search from a guess into a lookup.
    /// </summary>
    private void OnTraceRoute(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected?.Site.Base is not { } guess) return;

        // Detach before the search: indexing every pointer in the target is heavy, and there is
        // no reason to keep freezing the game while it runs.
        _viewModel.Stop();

        var window = new PointerScanWindow(_process, _viewModel.Address, _viewModel.Description,
            _viewModel.Type, _expectedBits, guess.Offset) { Owner = this };

        if (window.ShowDialog() != true || window.Chosen is not { } path) return;

        Chosen = path;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// The pairing that makes both halves worth more: knowing the object, everything else about it
    /// is sitting next to the value — all the stats of one character rather than one at a time.
    /// </summary>
    private void OnShowObject(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected?.Site.Base is not { } guess) return;

        var window = new MemoryViewWindow(_process, guess.Value,
            $"{_viewModel.Description} — the object it belongs to") { Owner = this };
        window.AddRequested += (address, type, label) => AddRequested?.Invoke(address, type, label);
        window.ShowDialog();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Closing the window has to detach. Nothing may leave a debugger on the game.</summary>
    private void OnClosed(object? sender, EventArgs e) => _viewModel.Dispose();
}
