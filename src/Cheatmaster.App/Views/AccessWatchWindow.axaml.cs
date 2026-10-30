using Avalonia.Controls;
using Avalonia.Interactivity;
using Cheatmaster.App.Services;
using Cheatmaster.App.ViewModels;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.Views;

public partial class AccessWatchWindow : Window
{
    private readonly AccessWatchViewModel? _viewModel;
    private readonly TargetProcess? _process;
    private readonly ulong _expectedBits;

    public AccessWatchWindow()
    {
        InitializeComponent();
        WindowChrome.DragBy(this, TitleBar);
        // Closing the window has to detach. Nothing may leave a debugger on the game.
        Closed += (_, _) => _viewModel?.Dispose();
    }

    public AccessWatchWindow(TargetProcess process, ulong address, string description, ScanType type, ulong expectedBits) : this()
    {
        _process = process;
        _expectedBits = expectedBits;
        _viewModel = new AccessWatchViewModel(process, address, description, type);
        DataContext = _viewModel;
    }

    /// <summary>Shows the window; completes with the route the user traced, or null.</summary>
    public Task<PointerPath?> ChooseAsync(Window owner) => ShowDialog<PointerPath?>(owner);

    /// <summary>A field picked out of the object, on its way to the cheat table.</summary>
    public event Action<ulong, ScanType, string>? AddRequested;

    /// <summary>
    /// Hands the offset the instruction just revealed to the pointer search. Knowing where the
    /// value sits inside its object is what turns that search from a guess into a lookup.
    /// </summary>
    private async void OnTraceRoute(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.Selected?.Site.Base is not { } guess || _process is null) return;

        // Detach before the search: indexing every pointer in the target is heavy, and there is
        // no reason to keep freezing the game while it runs.
        await _viewModel.StopAsync();

        var window = new PointerScanWindow(_process, _viewModel.Address, _viewModel.Description,
            _viewModel.Type, _expectedBits, guess.Offset);
        if (await window.ChooseAsync(this) is not { } path) return;

        Close(path);
    }

    /// <summary>
    /// The pairing that makes both halves worth more: knowing the object, everything else about it
    /// is sitting next to the value — all the stats of one character rather than one at a time.
    /// </summary>
    private async void OnShowObject(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.Selected?.Site.Base is not { } guess || _process is null) return;

        var window = new MemoryViewWindow(_process, guess.Value, $"{_viewModel.Description} — the object it belongs to");
        window.AddRequested += (address, type, label) => AddRequested?.Invoke(address, type, label);
        await window.ShowDialog(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
