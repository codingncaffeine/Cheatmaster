using Avalonia.Controls;
using Avalonia.Interactivity;
using Cheatmaster.App.Services;
using Cheatmaster.Core.Cheats;

namespace Cheatmaster.App.Views;

public partial class ImportReportWindow : Window
{
    public ImportReportWindow()
    {
        InitializeComponent();
        WindowChrome.DragBy(this, TitleBar);
    }

    public ImportReportWindow(ImportReport report) : this() => DataContext = report;

    public static Task Show(Window owner, ImportReport report) => new ImportReportWindow(report).ShowDialog(owner);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
