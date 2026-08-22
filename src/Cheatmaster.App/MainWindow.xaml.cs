using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Cheatmaster.App.Services;
using Cheatmaster.App.ViewModels;
using Cheatmaster.App.Views;
using Cheatmaster.Core.Sync;

namespace Cheatmaster.App;

public partial class MainWindow : Window
{
    private const double EstimatedRowHeight = 26;

    private readonly MainViewModel _viewModel = new();
    private readonly HotkeyService _hotkeys = new();
    private ScrollViewer? _resultsScroll;

    /// <summary>Exposed so startup diagnostics can report through the same notice bar the user reads.</summary>
    public MainViewModel ViewModel => _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Width = _viewModel.Settings.WindowWidth;
        Height = _viewModel.Settings.WindowHeight;
        if (_viewModel.Settings.WindowMaximized) WindowState = WindowState.Maximized;

        ChromeSupport.Apply(this);

        _viewModel.AttachRequested += ShowProcessPicker;
        _viewModel.CheatSetChanged += SyncHotkeys;
        _viewModel.Library.PromptForValue = (title, message, initial) =>
            ValuePromptWindow.Ask(this, title, message, initial);
        _viewModel.PromptForValue = (title, message, initial) =>
            ValuePromptWindow.Ask(this, title, message, initial,
                "Accepts decimals and hex (0x1F). Each entry is written using its own storage type.");
        _hotkeys.Pressed += OnHotkeyPressed;

        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                _hotkeys.Attach(source);
                SyncHotkeys();
            }
        };

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            HookResultsScroll();

            // Selecting every row would realise a view model per hit, which is exactly what the
            // lazy result list exists to avoid.
            ResultsGrid.CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll, OnSelectAllResults));

            CheatsGrid.BeginningEdit += (_, _) => _viewModel.IsEditingCell = true;
            CheatsGrid.CellEditEnding += (_, _) => _viewModel.IsEditingCell = false;
            CheatsGrid.RowEditEnding += (_, _) => _viewModel.IsEditingCell = false;

            StartBackgroundBackup();
        };

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

    // ---------------------------------------------------------------- chrome

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnDismissNotice(object sender, RoutedEventArgs e) => _viewModel.ClearNotice();

    // ---------------------------------------------------------------- interaction

    private void OnValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_viewModel.ScanCommand.CanExecute(null)) _viewModel.ScanCommand.Execute(null);
        e.Handled = true;
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
                    _ = Dispatcher.BeginInvoke(() => _viewModel.Notify("Cloud backup: " + outcome.Message, NoticeKind.Success));
            }
            catch (Exception ex)
            {
                _ = Dispatcher.BeginInvoke(() => _viewModel.Notify("Cloud backup skipped: " + ex.Message, NoticeKind.Info));
            }
        });
    }

    public void ShowCloudBackup()
    {
        var window = new CloudSyncWindow(_viewModel.Settings) { Owner = this };
        window.ShowDialog();
    }

    private void ShowProcessPicker()
    {
        var picker = new ProcessPickerWindow { Owner = this };
        if (picker.ShowDialog() == true && picker.Selected is not null)
            _viewModel.Attach(picker.Selected);
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is ResultRow row) _viewModel.AddResult(row);
    }

    private void OnCopyAddress(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (object? item in ResultsGrid.SelectedItems)
        {
            if (item is ResultRow row) text.AppendLine(row.AddressText);
        }

        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text.ToString().TrimEnd());
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
    private void OnFindPointerPath(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Process is not { } process)
        {
            _viewModel.Notify("Attach to the game first — a route has to be traced through its memory.", NoticeKind.Warning);
            return;
        }

        if (CheatsGrid.SelectedItem is not CheatRow row)
        {
            _viewModel.Notify("Select the entry you want a stable address for.", NoticeKind.Info);
            return;
        }

        ulong address = row.Entry.Address.Resolve(process);
        if (address == 0 || !row.Entry.TryReadValue(process, out ulong bits))
        {
            _viewModel.Notify("That entry does not currently resolve to a readable address.", NoticeKind.Warning);
            return;
        }

        var window = new PointerScanWindow(process, address, row.Description, row.Entry.Type, bits) { Owner = this };
        if (window.ShowDialog() == true && window.Chosen is { } path)
            _viewModel.ApplyPointerPath(row, path);
    }

    private void OnSelectAllCheats(object sender, RoutedEventArgs e) => CheatsGrid.SelectAll();

    /// <summary>
    /// Waits for the game's own code to touch an address and reports what did. This is the step a
    /// scan cannot take: it says which of the candidate addresses the game actually uses, and
    /// usually names the object the value belongs to as well.
    /// </summary>
    private void OnWatchCheat(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Process is not { } process)
        {
            _viewModel.Notify("Attach to the game first — there is nothing to watch until then.", NoticeKind.Warning);
            return;
        }

        if (CheatsGrid.SelectedItem is not CheatRow row)
        {
            _viewModel.Notify("Select the entry you want to watch.", NoticeKind.Info);
            return;
        }

        ulong address = row.Entry.Address.Resolve(process);
        if (address == 0 || !row.Entry.TryReadValue(process, out ulong bits))
        {
            _viewModel.Notify("That entry does not currently resolve to a readable address.", NoticeKind.Warning);
            return;
        }

        var window = new AccessWatchWindow(process, address, row.Description, row.Entry.Type, bits) { Owner = this };
        if (window.ShowDialog() == true && window.Chosen is { } path)
            _viewModel.ApplyPointerPath(row, path);
    }

    private void OnWatchResult(object sender, RoutedEventArgs e)
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
            row.Interpretation.Type, row.CurrentValue) { Owner = this };

        // A route traced from a result has nowhere to live yet, so the result becomes an entry
        // and the route goes straight onto it.
        if (window.ShowDialog() == true && window.Chosen is { } path)
            _viewModel.AddResultWithRoute(row, path);
    }

    private void OnCopyCheatAddress(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (object? item in CheatsGrid.SelectedItems)
        {
            if (item is CheatRow row) text.AppendLine(row.AddressText);
        }

        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            _viewModel.Notify("Could not copy: " + ex.Message, NoticeKind.Warning);
        }
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
    private void SyncHotkeys()
    {
        var wanted = new List<string>();
        foreach (var row in _viewModel.Cheats)
        {
            if (!string.IsNullOrWhiteSpace(row.Hotkey)) wanted.Add(row.Hotkey);
        }

        var failed = _hotkeys.Sync(wanted);
        if (failed.Count > 0)
            _viewModel.Notify("Another application already owns " + string.Join(", ", failed) + ".", NoticeKind.Warning);
    }

    private void OnSelectAllResults(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_viewModel.Results is not { Count: > 0 } results) return;

        const int Limit = 200;
        ResultsGrid.SelectedItems.Clear();
        int take = Math.Min(Limit, results.Count);
        for (int i = 0; i < take; i++) ResultsGrid.SelectedItems.Add(results[i]);

        if (results.Count > take)
            _viewModel.Notify($"Selected the first {take:N0} of {results.Count:N0} results.", NoticeKind.Info);
    }

    // ---------------------------------------------------------------- result virtualisation

    /// <summary>
    /// Only the rows on screen get re-read on the refresh tick, so a scan that returned a
    /// million addresses still costs nothing to keep live.
    /// </summary>
    private void HookResultsScroll()
    {
        _resultsScroll = FindScrollViewer(ResultsGrid);
        if (_resultsScroll is null) return;
        _resultsScroll.ScrollChanged += (_, _) => ReportVisibleRange();
        ReportVisibleRange();
    }

    private void ReportVisibleRange()
    {
        if (_resultsScroll is null) return;
        int first = (int)(_resultsScroll.VerticalOffset / EstimatedRowHeight);
        int count = (int)(_resultsScroll.ViewportHeight / EstimatedRowHeight) + 2;
        _viewModel.SetVisibleRange(first, first + count);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }
}
