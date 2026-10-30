using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cheatmaster.App.Services;

namespace Cheatmaster.App.Views;

/// <summary>A single themed value prompt, used for bulk edits on the cheat table.</summary>
public partial class ValuePromptWindow : Window
{
    public ValuePromptWindow() : this("Set value", string.Empty, string.Empty) { }

    public ValuePromptWindow(string title, string message, string initialValue, string hint = "", string acceptLabel = "Apply")
    {
        InitializeComponent();
        WindowChrome.DragBy(this, TitleBar);

        Title = title;
        TitleLabel.Text = title;
        MessageLabel.Text = message;
        ValueBox.Text = initialValue;
        HintLabel.Text = hint;
        HintLabel.IsVisible = !string.IsNullOrEmpty(hint);
        AcceptButton.Content = acceptLabel;

        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    /// <summary>Shows the prompt and returns what was typed, or null if it was dismissed.</summary>
    public static Task<string?> Ask(Window owner, string title, string message, string initialValue,
        string hint = "", string acceptLabel = "Apply") =>
        new ValuePromptWindow(title, message, initialValue, hint, acceptLabel).ShowDialog<string?>(owner);

    private void OnValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnAccept(sender, e);
        e.Handled = true;
    }

    private void OnAccept(object? sender, RoutedEventArgs e) => Close(ValueBox.Text ?? string.Empty);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
