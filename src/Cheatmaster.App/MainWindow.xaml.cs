using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Cheatmaster.App.Services;
using Cheatmaster.App.ViewModels;
using Cheatmaster.App.Views;

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
