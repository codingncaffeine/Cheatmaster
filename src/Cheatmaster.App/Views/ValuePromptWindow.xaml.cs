using System.Windows;
using System.Windows.Input;

namespace Cheatmaster.App.Views;

/// <summary>A single themed value prompt, used for bulk edits on the cheat table.</summary>
public partial class ValuePromptWindow : Window
{
    public ValuePromptWindow(string title, string message, string initialValue, string hint = "", string acceptLabel = "Apply")
    {
        InitializeComponent();

        Title = title;
        TitleLabel.Text = title;
        MessageLabel.Text = message;
        ValueBox.Text = initialValue;
        HintLabel.Text = hint;
        HintLabel.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        AcceptButton.Content = acceptLabel;

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text;

    /// <summary>Shows the prompt and returns what was typed, or null if it was dismissed.</summary>
    public static string? Ask(Window? owner, string title, string message, string initialValue,
        string hint = "", string acceptLabel = "Apply")
    {
        var window = new ValuePromptWindow(title, message, initialValue, hint, acceptLabel);
        if (owner is not null) window.Owner = owner;
        return window.ShowDialog() == true ? window.Value : null;
    }

    private void OnValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnAccept(sender, e);
        e.Handled = true;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
