using System.Windows.Controls;
using System.Windows.Input;
using Cheatmaster.App.ViewModels;

namespace Cheatmaster.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private void OnGameDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is LibraryViewModel library && library.Selected is LibraryGameRow row)
            library.Open(row);
    }
}
