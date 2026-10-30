using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cheatmaster.App.Services;
using Cheatmaster.Core.Memory;

namespace Cheatmaster.App.Views;

public partial class ProcessPickerWindow : Window
{
    private List<ProcessCandidate> _all = [];
    private int _loads;

    public ProcessPickerWindow()
    {
        InitializeComponent();
        WindowChrome.DragBy(this, TitleBar);
        Opened += (_, _) =>
        {
            Reload();
            SearchBox.Focus();
        };
    }

    /// <summary>Lists processes on a worker: reading every process's details takes a moment.</summary>
    private async void Reload()
    {
        int load = ++_loads;
        bool everything = ShowAllBox.IsChecked == true;
        CountLabel.Text = "Looking…";
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            var found = await Task.Run(() => ProcessList.Enumerate(everything));
            if (load != _loads) return;
            _all = found;
            ApplyFilter();
        }
        finally
        {
            if (load == _loads) Cursor = Cursor.Default;
        }
    }

    private void ApplyFilter()
    {
        string needle = (SearchBox.Text ?? string.Empty).Trim();
        IEnumerable<ProcessCandidate> view = _all;

        if (needle.Length > 0)
        {
            view = _all.Where(p =>
                p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                p.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString(CultureInfo.InvariantCulture) == needle);
        }

        var list = view.ToList();
        ProcessListBox.ItemsSource = list;
        CountLabel.Text = $"{list.Count} process{(list.Count == 1 ? "" : "es")}";

        if (list.Count > 0) ProcessListBox.SelectedIndex = 0;
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => Reload();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        AttachButton.IsEnabled = ProcessListBox.SelectedItem is ProcessCandidate;

    private void OnListDoubleClick(object? sender, TappedEventArgs e) => Accept();

    private void OnAttach(object? sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (ProcessListBox.SelectedItem is not ProcessCandidate candidate) return;
        Close(candidate);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
