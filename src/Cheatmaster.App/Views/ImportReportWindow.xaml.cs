using System.Windows;
using Cheatmaster.Core.Cheats;

namespace Cheatmaster.App.Views;

public partial class ImportReportWindow : Window
{
    private ImportReportWindow(ImportReport report)
    {
        InitializeComponent();
        DataContext = report;
    }

    public static void Show(Window owner, ImportReport report) =>
        new ImportReportWindow(report) { Owner = owner }.ShowDialog();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
