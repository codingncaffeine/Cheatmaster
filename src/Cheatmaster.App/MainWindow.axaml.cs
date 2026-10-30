using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Cheatmaster.App.Services;
using Cheatmaster.App.ViewModels;
using Cheatmaster.App.Views;
using Cheatmaster.Core.Sync;

namespace Cheatmaster.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly HotkeyService _hotkeys = new();

    /// <summary>Result rows the grid currently has on screen, by index.</summary>
    private readonly HashSet<int> _loadedRows = [];

    /// <summary>Exposed so startup diagnostics can report through the same notice bar the user reads.</summary>
    public MainViewModel ViewModel => _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Dialogs.Owner = this;
        WindowChrome.DragBy(this, TitleBar, maximizeOnDoubleClick: true);

        Width = _viewModel.Settings.WindowWidth;
        Height = _viewModel.Settings.WindowHeight;
        if (_viewModel.Settings.WindowMaximized) WindowState = WindowState.Maximized;

        _viewModel.AttachRequested += ShowProcessPicker;
        _viewModel.CheatSetChanged += SyncHotkeys;
        _viewModel.Guide.StableAddressRequested += OnGuideStableAddressRequested;
        _viewModel.Library.PromptForValue = (title, message, initial) =>
            ValuePromptWindow.Ask(this, title, message, initial);
        _viewModel.PromptForValue = (title, message, initial) =>
            ValuePromptWindow.Ask(this, title, message, initial,
                "Accepts decimals and hex (0x1F). Each entry is written using its own storage type.");
        _viewModel.ShowImportReport = report => _ = ImportReportWindow.Show(this, report);
        _hotkeys.Pressed += OnHotkeyPressed;

        // Only the rows on screen get re-read on the refresh tick, so a scan that returned a
        // million addresses still costs nothing to keep live.
        ResultsGrid.ContainerPrepared += (_, e) =>
        {
            _loadedRows.Add(e.Index);
            ReportVisibleRange();
        };
        ResultsGrid.ContainerClearing += (_, e) =>
        {
            if (e.Container.DataContext is ResultRow row) _loadedRows.Remove(row.Index);
            ReportVisibleRange();
        };

        // Selecting every row would realise a view model per hit, which is exactly what the lazy
        // result list exists to avoid.
        ResultsGrid.AddHandler(KeyDownEvent, OnResultsKeyDown, RoutingStrategies.Tunnel);

        CheatsGrid.BeginningEdit += (_, _) => _viewModel.IsEditingCell = true;
        CheatsGrid.CellEditEnded += (_, _) => _viewModel.IsEditingCell = false;
        CheatsGrid.RowEditEnded += (_, _) => _viewModel.IsEditingCell = false;

        Opened += OnFirstOpened;

        Closing += (_, _) =>
        {
            _viewModel.Settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _viewModel.Settings.WindowWidth = Width;
                _viewModel.Settings.WindowHeight = Height;
            }
            _hotkeys.Dispose();
            _viewModel.Dispose();
        };
    }

    /// <summary>Opened fires again whenever a hidden window is shown, so the start-up work unhooks itself.</summary>
    private void OnFirstOpened(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpened;
        ValueBox.Focus();
        SyncHotkeys();
        StartBackgroundBackup();
    }

    // ---------------------------------------------------------------- chrome

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnDismissNotice(object? sender, RoutedEventArgs e) => _viewModel.ClearNotice();

    // ---------------------------------------------------------------- interaction

    private void OnValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_viewModel.ScanCommand.CanExecute(null)) _viewModel.ScanCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Enter in the guide's value box does what its button does, so the game stays in focus.</summary>
    private void OnGuideValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_viewModel.Guide.PrimaryCommand.CanExecute(null)) _viewModel.Guide.PrimaryCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// The guide's last step: trace a route to the entry it has just saved, so the cheat still
    /// works after a restart. The newest entry is the one it added.
    /// </summary>
    private void OnGuideStableAddressRequested()
    {
        if (_viewModel.Cheats.Count == 0) return;

        var row = _viewModel.Cheats[^1];
        CheatsGrid.SelectedItem = row;
        CheatsGrid.ScrollIntoView(row, null);
        OnFindPointerPath(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Backs the library up in the background at startup when the user has asked for it. Nothing
    /// waits on it: if GitHub is slow or unreachable the app is simply unaffected.
    /// </summary>
    private void StartBackgroundBackup()
    {
        if (!_viewModel.Settings.AutoBackup) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var sync = new GitHubSyncService();
                if (!sync.IsSignedIn) return;

                var outcome = await sync.SyncAsync();
                _viewModel.Settings.LastSyncUtc = DateTimeOffset.UtcNow;

                if (outcome.Uploaded > 0 || outcome.Downloaded > 0)
                    Dispatcher.UIThread.Post(() => _viewModel.Notify("Cloud backup: " + outcome.Message, NoticeKind.Success));
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _viewModel.Notify("Cloud backup skipped: " + ex.Message, NoticeKind.Info));
            }
        });
    }

    public void ShowCloudBackup() => _ = new CloudSyncWindow(_viewModel.Settings).ShowDialog(this);

    private async void ShowProcessPicker()
    {
        var picked = await new ProcessPickerWindow().ShowDialog<Core.Memory.ProcessCandidate?>(this);
        if (picked is not null) _viewModel.Attach(picked);
    }

    private void OnResultDoubleClick(object? sender, TappedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is ResultRow row) _viewModel.AddResult(row);
    }

    private void OnResultsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            SelectFirstResults();
        }
        else if (e.Key == Key.Enter && ResultsGrid.SelectedItems is { Count: > 0 })
        {
            e.Handled = true;
            _viewModel.AddSelectedCommand.Execute(ResultsGrid.SelectedItems);
        }
    }

    private async void OnCopyAddress(object? sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (object? item in ResultsGrid.Selection.SelectedItems)
        {
            if (item is ResultRow row) text.AppendLine(row.AddressText);
        }
        await CopyAsync(text);
    }

    private async Task CopyAsync(StringBuilder text)
    {
        if (text.Length == 0) return;
        try
        {
            await Dialogs.CopyTextAsync(text.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            _viewModel.Notify("Could not copy: " + ex.Message, NoticeKind.Warning);
        }
    }

    /// <summary>
    /// Turns a session-only address into a route that survives a restart. Only meaningful while
    /// the game is running, because the route has to be traced through live memory.
    /// </summary>
    private async void OnFindPointerPath(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Process is not { } process)
        {
            _viewModel.Notify("Attach to the game first — a route has to be traced through its memory.", NoticeKind.Warning);
            return;
        }

        if (SelectedLiveCheat("find a stable address for") is not { } row) return;

        ulong address = row.Entry.Address.Resolve(process);
        if (address == 0 || !row.Entry.TryReadValue(process, out ulong bits))
        {
            _viewModel.Notify("That entry does not currently resolve to a readable address.", NoticeKind.Warning);
            return;
        }

        var window = new PointerScanWindow(process, address, row.Description, row.Entry.Type, bits);
        if (await window.ChooseAsync(this) is { } path)
            _viewModel.ApplyPointerPath(row, path);
    }

    private void OnSelectAllCheats(object? sender, RoutedEventArgs e) => CheatsGrid.SelectAll();

    private void OnFindAgain(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CheatRow row) _viewModel.FindAgain(row);
    }

    /// <summary>The selected cheat, unless it is a saved address that has not been found again yet.</summary>
    private CheatRow? SelectedLiveCheat(string purpose)
    {
        if (CheatsGrid.SelectedItem is not CheatRow row)
        {
            _viewModel.Notify($"Select the entry you want to {purpose}.", NoticeKind.Info);
            return null;
        }
        if (row.NeedsFinding)
        {
            _viewModel.Notify("That entry still points where the value was last time. Press Find it again on it first.", NoticeKind.Info);
            return null;
        }
        return row;
    }

    /// <summary>
    /// Opens the bytes around an address. This is where a value stops being one number and starts
    /// being a field of an object with everything else about that object sitting next to it.
    /// </summary>
    private async void ShowMemory(ulong address, string description)
    {
        if (_viewModel.Process is not { } process)
        {
            _viewModel.Notify("Attach to the game first — there is nothing to look at until then.", NoticeKind.Warning);
            return;
        }

        var window = new MemoryViewWindow(process, address, description);
        window.AddRequested += (fieldAddress, type, label) => _viewModel.AddAddress(fieldAddress, type, label);
        await window.ShowDialog(this);
    }

    private void OnShowCheatInMemory(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Process is not { } process)
        {
            _viewModel.Notify("Attach to the game first — there is nothing to look at until then.", NoticeKind.Warning);
            return;
        }

        if (SelectedLiveCheat("look at") is not { } row) return;

        ulong address = row.Entry.Address.Resolve(process);
        if (address == 0)
        {
            _viewModel.Notify("That entry does not currently resolve to an address.", NoticeKind.Warning);
            return;
        }

        ShowMemory(address, row.Description);
    }

    private void OnShowResultInMemory(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not ResultRow row)
        {
            _viewModel.Notify("Select the result you want to look at.", NoticeKind.Info);
            return;
        }

        ShowMemory(row.Address, $"Address {row.AddressText}");
    }

    /// <summary>
    /// Waits for the game's own code to touch an address and reports what did. This is the step a
    /// scan cannot take: it says which of the candidate addresses the game actually uses, and
    /// usually names the object the value belongs to as well.
    /// </summary>
    private async void OnWatchCheat(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Process is not { } process)
        {
            _viewModel.Notify("Attach to the game first — there is nothing to watch until then.", NoticeKind.Warning);
            return;
        }

        if (SelectedLiveCheat("watch") is not { } row) return;

        ulong address = row.Entry.Address.Resolve(process);
        if (address == 0 || !row.Entry.TryReadValue(process, out ulong bits))
        {
            _viewModel.Notify("That entry does not currently resolve to a readable address.", NoticeKind.Warning);
            return;
        }

        var window = new AccessWatchWindow(process, address, row.Description, row.Entry.Type, bits);
        window.AddRequested += _viewModel.AddAddress;
        if (await window.ChooseAsync(this) is { } path)
            _viewModel.ApplyPointerPath(row, path);
    }

    private async void OnWatchResult(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Process is not { } process)
        {
            _viewModel.Notify("Attach to the game first — there is nothing to watch until then.", NoticeKind.Warning);
            return;
        }

        if (ResultsGrid.SelectedItem is not ResultRow row)
        {
            _viewModel.Notify("Select the result you want to watch.", NoticeKind.Info);
            return;
        }

        var window = new AccessWatchWindow(process, row.Address, $"Address {row.AddressText}",
            row.Interpretation.Type, row.CurrentValue);
        window.AddRequested += _viewModel.AddAddress;

        // A route traced from a result has nowhere to live yet, so the result becomes an entry
        // and the route goes straight onto it.
        if (await window.ChooseAsync(this) is { } path)
            _viewModel.AddResultWithRoute(row, path);
    }

    private async void OnCopyCheatAddress(object? sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (object? item in CheatsGrid.SelectedItems)
        {
            if (item is CheatRow row) text.AppendLine(row.AddressText);
        }
        await CopyAsync(text);
    }

    private void OnHotkeyPressed(string combination)
    {
        foreach (var row in _viewModel.Cheats)
        {
            if (string.Equals(row.Hotkey, combination, StringComparison.OrdinalIgnoreCase))
                row.Frozen = !row.Frozen;
        }
    }

    /// <summary>
    /// Registers exactly the hotkeys the table asks for. Registration is system-wide, so a
    /// combination another application already owns is reported rather than failing quietly.
    /// </summary>
    private async void SyncHotkeys()
    {
        var wanted = new List<string>();
        foreach (var row in _viewModel.Cheats)
        {
            if (!string.IsNullOrWhiteSpace(row.Hotkey)) wanted.Add(row.Hotkey);
        }

        var failed = await _hotkeys.SyncAsync(wanted);
        if (failed.Count == 0) return;

        _viewModel.Notify(_hotkeys.IsAvailable
            ? "These hotkeys could not be registered — another application owns them, or the key is not recognised: " + string.Join(", ", failed) + "."
            : "Global hotkeys need an X11 session or XWayland, which this desktop does not offer: " + string.Join(", ", failed) + ".",
            NoticeKind.Warning);
    }

    private void SelectFirstResults()
    {
        if (_viewModel.Results is not { Count: > 0 } results) return;

        const int Limit = 200;
        int take = Math.Min(Limit, results.Count);
        ResultsGrid.Selection.Clear();
        ResultsGrid.Selection.SelectRange(0, take - 1);

        if (results.Count > take)
            _viewModel.Notify($"Selected the first {take:N0} of {results.Count:N0} results.", NoticeKind.Info);
    }

    private void ReportVisibleRange()
    {
        if (_loadedRows.Count == 0) return;
        _viewModel.SetVisibleRange(_loadedRows.Min(), _loadedRows.Max());
    }
}
