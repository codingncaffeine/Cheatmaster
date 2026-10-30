using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Cheatmaster.App.Views;

namespace Cheatmaster.App.Services;

/// <summary>A file type a picker offers: a name and its patterns, such as ("Cheatmaster table", "*.cmt").</summary>
public sealed record FileKind(string Name, params string[] Patterns);

/// <summary>
/// The few things a view model needs from the desktop — a question, a file name, the clipboard,
/// a browser — without holding a window. The main window registers itself as the owner.
/// </summary>
public static class Dialogs
{
    public static Window? Owner { get; set; }

    public static async Task<bool> ConfirmAsync(string message, bool warning = false)
    {
        if (Owner is null) return false;
        return await MessageWindow.AskAsync(Owner, "Cheatmaster", message, warning);
    }

    public static async Task ShowErrorAsync(string title, string message)
    {
        if (Owner is null) return;
        await MessageWindow.ShowAsync(Owner, title, message);
    }

    public static async Task<string?> SaveFileAsync(string title, string suggestedName, params FileKind[] kinds)
    {
        if (Owner?.StorageProvider is not { CanSave: true } storage) return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = ToTypes(kinds),
            ShowOverwritePrompt = true
        });
        return file?.TryGetLocalPath();
    }

    public static async Task<string?> OpenFileAsync(string title, params FileKind[] kinds)
    {
        if (Owner?.StorageProvider is not { CanOpen: true } storage) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ToTypes(kinds)
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static List<FilePickerFileType> ToTypes(FileKind[] kinds) =>
        kinds.Select(k => new FilePickerFileType(k.Name) { Patterns = k.Patterns }).ToList();

    public static async Task<bool> CopyTextAsync(string text)
    {
        if (Owner?.Clipboard is not { } clipboard) return false;
        await clipboard.SetTextAsync(text);
        return true;
    }

    /// <summary>Opens a web address in the user's browser.</summary>
    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>
    /// Shows a file in the system file manager, selected where the platform allows it. Runs off the
    /// UI thread; returns what went wrong, or null.
    /// </summary>
    public static Task<string?> RevealAsync(string path) => Task.Run(() =>
    {
        try
        {
            Reveal(path);
            return (string?)null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return ex.Message;
        }
    });

    private static void Reveal(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return;
        }

        // The freedesktop file-manager interface selects the file (Dolphin, Nautilus, Nemo, Thunar
        // all answer it); a desktop without one gets the folder opened instead.
        try
        {
            var info = new ProcessStartInfo("dbus-send")
            {
                UseShellExecute = false,
                RedirectStandardError = true
            };
            foreach (string arg in new[]
                     {
                         "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
                         "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                         "array:string:" + new Uri(path).AbsoluteUri, "string:"
                     })
                info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process is not null && process.WaitForExit(3000) && process.ExitCode == 0) return;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No dbus-send: fall back to the folder.
        }

        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
}
