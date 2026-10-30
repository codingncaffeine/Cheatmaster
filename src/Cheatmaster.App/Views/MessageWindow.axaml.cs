using Avalonia.Controls;
using Avalonia.Interactivity;
using Cheatmaster.App.Services;

namespace Cheatmaster.App.Views;

/// <summary>A themed yes/no question or a plain message, in place of the system message box.</summary>
public partial class MessageWindow : Window
{
    public MessageWindow()
    {
        InitializeComponent();
        WindowChrome.DragBy(this, TitleBar);
    }

    public static Task<bool> AskAsync(Window owner, string title, string message, bool warning)
    {
        var window = new MessageWindow();
        window.Title = title;
        window.TitleLabel.Text = title;
        window.MessageLabel.Text = message;
        if (warning)
        {
            window.YesButton.Classes.Remove("primary");
            window.YesButton.Classes.Add("danger");
            window.YesButton.Content = "Delete";
        }
        return window.ShowDialog<bool>(owner);
    }

    public static Task ShowAsync(Window owner, string title, string message)
    {
        var window = new MessageWindow();
        window.Title = title;
        window.TitleLabel.Text = title;
        window.MessageLabel.Text = message;
        window.NoButton.IsVisible = false;
        window.YesButton.Content = "OK";
        return window.ShowDialog<bool>(owner);
    }

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);
}
