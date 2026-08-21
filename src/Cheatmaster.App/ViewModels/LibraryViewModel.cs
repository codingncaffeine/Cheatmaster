using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Cheatmaster.App.Infrastructure;
using Cheatmaster.Core.Cheats;

namespace Cheatmaster.App.ViewModels;

/// <summary>One saved cheat, shown read-only in the library detail pane.</summary>
public sealed record SavedCheatRow(string Description, string Address, string Type, string FrozenAt, string Hotkey)
{
    public bool IsFrozen => !string.IsNullOrEmpty(FrozenAt);
}

/// <summary>One saved game as it appears in the library grid.</summary>
public sealed class LibraryGameRow : ObservableObject
{
    private BitmapImage? _cover;

    public LibraryGameRow(LibraryEntry entry)
    {
        Entry = entry;
        _cover = ImageLoader.Load(entry.ArtPath, 300);
    }

    public LibraryEntry Entry { get; private set; }

    public string Key => Entry.Key;
    public string Name => Entry.GameName;
    public string Subtitle => Entry.Subtitle;
    public string CheatSummary => Entry.CheatSummary;
    public string Description => Entry.Description;
    public string ExecutableName => Entry.ExecutableName;
    public string ReleaseDate => Entry.ReleaseDate;
    public string ModifiedText => Entry.Modified.LocalDateTime.ToString("d MMM yyyy");

    public BitmapImage? Cover
    {
        get => _cover;
        private set
        {
            if (Set(ref _cover, value)) Raise(nameof(HasCover));
        }
    }

    public bool HasCover => _cover is not null;

    /// <summary>
    /// Free-form notes for this game — how a cheat was found, what a value does, what still
    /// needs work. Saved into the game's own table so they travel with it.
    /// </summary>
    public string Notes
    {
        get => Entry.Notes;
        set
        {
            if (Entry.Notes == value) return;
            Entry = Entry with { Notes = value };
            Raise();

            var table = CheatTable.Load(Entry.Path);
            if (table is null) return;
            table.Notes = value;
            table.Save(Entry.Path);
        }
    }

    /// <summary>Two initials, shown in place of a cover the store had nothing for.</summary>
    public string Initials
    {
        get
        {
            string name = Entry.GameName.Trim();
            if (name.Length == 0) return "?";

            string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 1) return words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant();
            return string.Concat(words[0][0], words[1][0]).ToUpperInvariant();
        }
    }

    public void Update(LibraryEntry entry)
    {
        Entry = entry;
        Cover = ImageLoader.Load(entry.ArtPath, 300);
        Raise(nameof(Name));
        Raise(nameof(Subtitle));
        Raise(nameof(Description));
        Raise(nameof(CheatSummary));
        Raise(nameof(ReleaseDate));
        Raise(nameof(Initials));
        Raise(nameof(Notes));
    }
}

/// <summary>
/// The saved-cheats side of the app: every game with a table, with cover art and a blurb
/// filled in from the store listing the first time the game is seen.
/// </summary>
public sealed class LibraryViewModel : ObservableObject
{
    private readonly CheatLibrary _library;
    private readonly GameMetadataService _metadata = new();
    private readonly List<LibraryGameRow> _all = [];

    private LibraryGameRow? _selected;
    private string _searchText = string.Empty;
    private string _status = string.Empty;
    private bool _isBusy;

    public LibraryViewModel(CheatLibrary library)
    {
        _library = library;

        RefreshCommand = new RelayCommand(async () => await ReloadAsync());
        DeleteCommand = new RelayCommand(DeleteSelected, () => Selected is not null);
        ExportCommand = new RelayCommand(ExportSelected, () => Selected is not null);
        RevealCommand = new RelayCommand(RevealSelected, () => Selected is not null);
        SetCoverCommand = new RelayCommand(SetCoverForSelected, () => Selected is not null);
        FetchArtCommand = new RelayCommand(async () => await FetchMissingArtAsync(force: true), () => !IsBusy);
    }

    public ObservableCollection<LibraryGameRow> Games { get; } = [];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand SetCoverCommand { get; }
    public RelayCommand FetchArtCommand { get; }

    /// <summary>Raised when the user asks to work on a game that is not the attached one.</summary>
    public event Action<LibraryGameRow>? OpenRequested;

    public bool FetchArtworkEnabled { get; set; } = true;

    public LibraryGameRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection));
            LoadSelectedCheats();
            DeleteCommand.RaiseCanExecuteChanged();
            ExportCommand.RaiseCanExecuteChanged();
            RevealCommand.RaiseCanExecuteChanged();
            SetCoverCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The cheats saved for the selected game, for the detail pane.</summary>
    public ObservableCollection<SavedCheatRow> SelectedCheats { get; } = [];

    private void LoadSelectedCheats()
    {
        SelectedCheats.Clear();
        if (Selected is null) return;

        var table = CheatTable.Load(Selected.Entry.Path);
        if (table is null) return;

        foreach (var entry in table.Entries)
        {
            SelectedCheats.Add(new SavedCheatRow(
                entry.Description,
                entry.Address.Display,
                entry.Interpretation.Label,
                entry.Frozen ? entry.FreezeValue : string.Empty,
                string.IsNullOrWhiteSpace(entry.Hotkey) ? "—" : entry.Hotkey));
        }
    }

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => Games.Count == 0;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) ApplyFilter();
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) FetchArtCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task ReloadAsync()
    {
        _all.Clear();
        foreach (var entry in _library.List()) _all.Add(new LibraryGameRow(entry));

        ApplyFilter();
        Status = _all.Count == 0
            ? "No saved games yet."
            : $"{_all.Count} game{(_all.Count == 1 ? "" : "s")} · {_all.Sum(static g => g.Entry.CheatCount)} cheats saved";

        if (FetchArtworkEnabled) await FetchMissingArtAsync(force: false);
    }

    private void ApplyFilter()
    {
        string needle = SearchText.Trim();
        Games.Clear();

        foreach (var game in _all)
        {
            if (needle.Length > 0 &&
                !game.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) &&
                !game.ExecutableName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            Games.Add(game);
        }

        Raise(nameof(IsEmpty));
        if (Selected is not null && !Games.Contains(Selected)) Selected = null;
        Selected ??= Games.FirstOrDefault();
    }

    /// <summary>
    /// Looks up cover art and a description for games that have none. Each game is attempted
    /// once unless the user asks again, so an unlisted title does not retry on every visit.
    /// </summary>
    public async Task FetchMissingArtAsync(bool force)
    {
        if (IsBusy) return;

        var pending = new List<LibraryGameRow>();
        foreach (var game in _all)
        {
            var table = CheatTable.Load(game.Entry.Path);
            if (table is null) continue;
            if (!force && (table.MetadataFetched || game.HasCover)) continue;
            pending.Add(game);
        }

        if (pending.Count == 0) return;

        IsBusy = true;
        int found = 0;
        try
        {
            foreach (var game in pending)
            {
                var table = CheatTable.Load(game.Entry.Path);
                if (table is null) continue;

                Status = $"Looking up {table.GameName}…";
                var fingerprint = new GameFingerprint(table.ExecutableName, table.GameName, table.GameVersion,
                    table.ExecutableHash, FindInstallPath(table));

                var metadata = await _metadata.FetchAsync(fingerprint);
                table.MetadataFetched = true;

                if (metadata is not null)
                {
                    if (!string.IsNullOrWhiteSpace(metadata.Name)) table.GameName = metadata.Name;
                    table.Description = metadata.Description;
                    table.Developer = metadata.Developer;
                    table.ReleaseDate = metadata.ReleaseDate;
                    table.SteamAppId = metadata.AppId;
                    table.ArtPath = metadata.ArtPath;
                    found++;
                }

                table.Save(game.Entry.Path);
                game.Update(ToEntry(table, game.Entry.Path));
            }
        }
        finally
        {
            IsBusy = false;
            Status = found > 0
                ? $"Found artwork for {found} game{(found == 1 ? "" : "s")}."
                : $"{_all.Count} game{(_all.Count == 1 ? "" : "s")} in the library.";
        }
    }

    private static string FindInstallPath(CheatTable table) => table.ExecutablePath;

    private static LibraryEntry ToEntry(CheatTable table, string path) => new(
        Path.GetFileNameWithoutExtension(path),
        string.IsNullOrWhiteSpace(table.GameName) ? Path.GetFileNameWithoutExtension(path) : table.GameName,
        table.ExecutableName,
        table.GameVersion,
        table.Entries.Count,
        table.Modified,
        path,
        table.Description,
        table.Developer,
        table.ReleaseDate,
        table.ArtPath,
        table.Notes);

    public void Open(LibraryGameRow row) => OpenRequested?.Invoke(row);

    private void DeleteSelected()
    {
        if (Selected is null) return;

        var answer = MessageBox.Show(
            $"Delete the saved cheats for {Selected.Name}?\n\nThis removes {Selected.Entry.CheatSummary} and cannot be undone.",
            "Cheatmaster", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        string key = Selected.Key;
        _library.Delete(key);
        _all.RemoveAll(g => g.Key == key);
        ApplyFilter();
        Status = "Deleted.";
    }

    private void ExportSelected()
    {
        if (Selected is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export cheat table",
            Filter = "Cheatmaster table (*.cmt)|*.cmt",
            FileName = Selected.Name + CheatTable.FileExtension
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.Copy(Selected.Entry.Path, dialog.FileName, overwrite: true);
            Status = "Exported to " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            Status = "Export failed: " + ex.Message;
        }
    }

    private void RevealSelected()
    {
        if (Selected is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Selected.Entry.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Could not open the folder: " + ex.Message;
        }
    }

    private void SetCoverForSelected()
    {
        if (Selected is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a cover image",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            string destination = _metadata.ArtPathFor(Selected.Key);
            File.Copy(dialog.FileName, destination, overwrite: true);

            var table = CheatTable.Load(Selected.Entry.Path);
            if (table is not null)
            {
                table.ArtPath = destination;
                table.MetadataFetched = true;
                table.Save(Selected.Entry.Path);
                Selected.Update(ToEntry(table, Selected.Entry.Path));
            }
            Status = "Cover updated.";
        }
        catch (Exception ex)
        {
            Status = "Could not set the cover: " + ex.Message;
        }
    }
}
