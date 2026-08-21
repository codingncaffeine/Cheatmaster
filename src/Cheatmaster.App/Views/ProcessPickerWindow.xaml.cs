using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cheatmaster.Core.Memory;

namespace Cheatmaster.App.Views;

public partial class ProcessPickerWindow : Window
{
    private List<ProcessCandidate> _all = [];

    public ProcessPickerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Reload();
            SearchBox.Focus();
        };
    }

    public ProcessCandidate? Selected { get; private set; }

    private void Reload()
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _all = ProcessList.Enumerate(ShowAllBox.IsChecked == true);
            ApplyFilter();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void ApplyFilter()
    {
        string needle = SearchBox.Text.Trim();
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

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AttachButton.IsEnabled = ProcessListBox.SelectedItem is ProcessCandidate;

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void OnAttach(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (ProcessListBox.SelectedItem is not ProcessCandidate candidate) return;
        Selected = candidate;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
