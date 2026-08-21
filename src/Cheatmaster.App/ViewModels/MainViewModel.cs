using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Cheatmaster.App.Infrastructure;
using Cheatmaster.App.Services;
using Cheatmaster.Core.Cheats;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Native;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.ViewModels;

public sealed record TypeOption(string Label, ScanType? Type);
public sealed record CompareOption(string Label, CompareKind Kind);
public sealed record ProfileOption(string Label, string Detail, ScanProfile Profile);
public sealed record RoundingOption(string Label, string Detail, RoundingMode Mode);

public sealed class MainViewModel : ObservableObject, ICheatHost, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly ValueFreezer _freezer = new();
    private readonly CheatLibrary _library = new();
    private readonly ScanSettings _scanSettings = new();

    private CancellationTokenSource? _scanCancellation;
    private CheatTable _table = new();
    private GameFingerprint? _game;
    private DateTime _lastSaveRequest = DateTime.MinValue;
    private bool _savePending;
    private int _tick;
    private int _visibleFirst;
    private int _visibleLast = 40;

    public MainViewModel()
    {
        Settings = AppSettings.Load();
        Settings.ApplyTo(_scanSettings);

        // Commands first: the selection setters below raise CanExecuteChanged on them.
        AttachCommand = new RelayCommand(() => AttachRequested?.Invoke());
        DetachCommand = new RelayCommand(Detach, () => IsAttached);
        ScanCommand = new RelayCommand(async () => await RunScanAsync(first: !HasResults), () => CanScan);
        FirstScanCommand = new RelayCommand(async () => await RunScanAsync(first: !HasResults), () => CanScan);
        NextScanCommand = new RelayCommand(async () => await RunScanAsync(first: false), () => CanScan && HasResults);
        UndoScanCommand = new RelayCommand(UndoScan, () => Session?.CanUndo == true && !IsScanning);
        NewScanCommand = new RelayCommand(NewScan, () => HasResults && !IsScanning);
        CancelScanCommand = new RelayCommand(() => _scanCancellation?.Cancel(), () => IsScanning);
        AddSelectedCommand = new RelayCommand(AddSelectedResults);
        RemoveCheatCommand = new RelayCommand(RemoveCheats);
        ClearCheatsCommand = new RelayCommand(ClearCheats, () => Cheats.Count > 0);
        SaveTableCommand = new RelayCommand(SaveTableNow, () => IsAttached);
        ExportTableCommand = new RelayCommand(ExportTable, () => Cheats.Count > 0);
        ImportTableCommand = new RelayCommand(ImportTable);
        RestartElevatedCommand = new RelayCommand(RestartElevated);
        PinInterpretationCommand = new RelayCommand(o => PinInterpretation(o as InterpretationChip));
        KeepOnlyCommand = new RelayCommand(o => KeepOnly(o as InterpretationChip));
        FreezeSelectedCommand = new RelayCommand(o => FreezeSelected(o as IList));

        SelectedProfile = Profiles.FirstOrDefault(p => p.Profile == Settings.Profile) ?? Profiles[1];
        SelectedRounding = RoundingModes.FirstOrDefault(r => r.Mode == Settings.Rounding) ?? RoundingModes[1];
        SelectedAlignment = Alignments.Contains(Settings.Alignment) ? Settings.Alignment : 4;
        SelectedTypeOption = TypeOptions[0];
        SelectedCompare = CompareOptions[0];

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _timer.Tick += OnTick;
        _timer.Start();

        Status = "Not attached. Pick a process to begin.";
    }

    public event Action? AttachRequested;

    public AppSettings Settings { get; }
    public TargetProcess? Process { get; private set; }
    public ScanSession? Session { get; private set; }

    // ------------------------------------------------------------------ options

    public IReadOnlyList<TypeOption> TypeOptions { get; } =
    [
        new("Auto — work it out", null),
        new("Int32 (4 bytes)", ScanType.Int32),
        new("Float", ScanType.Float),
        new("Int64 (8 bytes)", ScanType.Int64),
        new("Double", ScanType.Double),
        new("Int16 (2 bytes)", ScanType.Int16),
        new("Int8 (1 byte)", ScanType.Int8),
        new("UInt32", ScanType.UInt32),
        new("UInt64", ScanType.UInt64),
        new("UInt16", ScanType.UInt16),
        new("UInt8", ScanType.UInt8)
    ];

    public ObservableCollection<CompareOption> CompareOptions { get; } =
    [
        new("Equal to", CompareKind.EqualTo),
        new("Greater than", CompareKind.GreaterThan),
        new("Less than", CompareKind.LessThan),
        new("Between", CompareKind.Between)
    ];

    public IReadOnlyList<ProfileOption> Profiles { get; } =
    [
        new("Fast", "Int32, Float, Int64, Double", ScanProfile.Fast),
        new("Standard", "Every type, plus scaled and percentage storage", ScanProfile.Standard),
        new("Thorough", "Adds byte-swapped and fixed-point storage", ScanProfile.Thorough)
    ];

    public IReadOnlyList<RoundingOption> RoundingModes { get; } =
    [
        new("Exact", "The stored number must match exactly", RoundingMode.Exact),
        new("Display", "Allow for a display that rounds or truncates", RoundingMode.Display),
        new("Loose", "Allow a couple of percent of drift", RoundingMode.Loose)
    ];

    public IReadOnlyList<int> Alignments { get; } = [1, 2, 4, 8];

    // ------------------------------------------------------------------ scan inputs

    private string _valueText = string.Empty;
    public string ValueText
    {
        get => _valueText;
        set { if (Set(ref _valueText, value)) RaiseCommands(); }
    }

    private string _value2Text = string.Empty;
    public string Value2Text
    {
        get => _value2Text;
        set { if (Set(ref _value2Text, value)) RaiseCommands(); }
    }

    private CompareOption _selectedCompare = null!;
    public CompareOption SelectedCompare
    {
        get => _selectedCompare;
        set
        {
            if (!Set(ref _selectedCompare, value)) return;
            Raise(nameof(NeedsSecondValue));
            Raise(nameof(NeedsValue));
            RaiseCommands();
        }
    }

    public bool NeedsValue => SelectedCompare?.Kind.NeedsValue() ?? true;
    public bool NeedsSecondValue => SelectedCompare?.Kind.NeedsSecondValue() ?? false;

    private TypeOption _selectedTypeOption = null!;
    public TypeOption SelectedTypeOption
    {
        get => _selectedTypeOption;
        set
        {
            if (!Set(ref _selectedTypeOption, value)) return;
            Raise(nameof(IsAutoType));
        }
    }

    public bool IsAutoType => SelectedTypeOption?.Type is null;

    private ProfileOption _selectedProfile = null!;
    public ProfileOption SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!Set(ref _selectedProfile, value)) return;
            Settings.Profile = value.Profile;
            Settings.Save();
        }
    }

    private RoundingOption _selectedRounding = null!;
    public RoundingOption SelectedRounding
    {
        get => _selectedRounding;
        set
        {
            if (!Set(ref _selectedRounding, value)) return;
            Settings.Rounding = value.Mode;
            Settings.Save();
        }
    }

    private int _selectedAlignment = 4;
    public int SelectedAlignment
    {
        get => _selectedAlignment;
        set
        {
            if (!Set(ref _selectedAlignment, value)) return;
            Settings.Alignment = value;
            _scanSettings.Alignment = value;
            Settings.Save();
        }
    }

    public bool WritableOnly
    {
        get => Settings.WritableOnly;
        set { Settings.WritableOnly = value; ApplyRegionSettings(); Raise(); }
    }

    public bool IncludeImage
    {
        get => Settings.IncludeImage;
        set { Settings.IncludeImage = value; ApplyRegionSettings(); Raise(); }
    }

    public bool IncludePrivate
    {
        get => Settings.IncludePrivate;
        set { Settings.IncludePrivate = value; ApplyRegionSettings(); Raise(); }
    }

    public bool IncludeMapped
    {
        get => Settings.IncludeMapped;
        set { Settings.IncludeMapped = value; ApplyRegionSettings(); Raise(); }
    }

    public bool LiveValues
    {
        get => Settings.LiveValues;
        set { Settings.LiveValues = value; Settings.Save(); Raise(); }
    }

    private void ApplyRegionSettings()
    {
        Settings.ApplyTo(_scanSettings);
        Settings.Save();
    }

    // ------------------------------------------------------------------ state

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value)) return;
            Raise(nameof(ScanButtonLabel));
            RaiseCommands();
        }
    }

    private double _scanProgress;
    public double ScanProgress
    {
        get => _scanProgress;
        private set => Set(ref _scanProgress, value);
    }

    private string _scanPhase = string.Empty;
    public string ScanPhase
    {
        get => _scanPhase;
        private set => Set(ref _scanPhase, value);
    }

    public string ScanButtonLabel => IsScanning ? "Scanning…" : HasResults ? "Next scan" : "First scan";

    public bool IsAttached => Process is not null;

    private string _processTitle = "No process attached";
    public string ProcessTitle
    {
        get => _processTitle;
        private set => Set(ref _processTitle, value);
    }

    private string _processSubtitle = "Click to choose a target";
    public string ProcessSubtitle
    {
        get => _processSubtitle;
        private set => Set(ref _processSubtitle, value);
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    private string _notice = string.Empty;
    public string Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    private NoticeKind _noticeKind = NoticeKind.Info;
    public NoticeKind NoticeLevel
    {
        get => _noticeKind;
        private set => Set(ref _noticeKind, value);
    }

    public bool ShowElevationHint => !Privileges.IsElevated;

    private LazyResultList? _results;
    public LazyResultList? Results
    {
        get => _results;
        private set
        {
            if (!Set(ref _results, value)) return;
            Raise(nameof(HasResults));
            Raise(nameof(ResultCountText));
            Raise(nameof(ScanButtonLabel));
            RaiseCommands();
        }
    }

    public bool HasResults => Results is { Count: > 0 };

    public string ResultCountText
    {
        get
        {
            if (Results is null) return "No scan yet";
            string total = Results.TotalCount.ToString("N0", CultureInfo.InvariantCulture);
            return Results.IsCapped
                ? $"{total} results (showing first {Results.Count:N0})"
                : $"{total} result" + (Results.TotalCount == 1 ? "" : "s");
        }
    }

    public ObservableCollection<InterpretationChip> Chips { get; } = [];

    private string _verdict = string.Empty;
    public string Verdict
    {
        get => _verdict;
        private set => Set(ref _verdict, value);
    }

    private string _verdictDetail = string.Empty;
    public string VerdictDetail
    {
        get => _verdictDetail;
        private set => Set(ref _verdictDetail, value);
    }

    public ObservableCollection<CheatRow> Cheats { get; } = [];

    private string _gameName = string.Empty;
    public string GameName
    {
        get => _gameName;
        private set => Set(ref _gameName, value);
    }

    public string FreezeStatus =>
        _freezer.PinnedCount == 0 ? string.Empty : $"{_freezer.PinnedCount} frozen";

    // ------------------------------------------------------------------ commands

    public RelayCommand AttachCommand { get; }
    public RelayCommand DetachCommand { get; }
    /// <summary>Runs a first scan or narrows the existing results, whichever the state calls for.</summary>
    public RelayCommand ScanCommand { get; }

    public RelayCommand FirstScanCommand { get; }
    public RelayCommand NextScanCommand { get; }
    public RelayCommand UndoScanCommand { get; }
    public RelayCommand NewScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand AddSelectedCommand { get; }
    public RelayCommand RemoveCheatCommand { get; }
    public RelayCommand ClearCheatsCommand { get; }
    public RelayCommand SaveTableCommand { get; }
    public RelayCommand ExportTableCommand { get; }
    public RelayCommand ImportTableCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }
    public RelayCommand PinInterpretationCommand { get; }
    public RelayCommand KeepOnlyCommand { get; }
    public RelayCommand FreezeSelectedCommand { get; }

    private bool CanScan => IsAttached && !IsScanning &&
        (!NeedsValue || UserValue.Parse(ValueText).IsValid) &&
        (!NeedsSecondValue || UserValue.Parse(Value2Text).IsValid);

    private void RaiseCommands()
    {
        ScanCommand.RaiseCanExecuteChanged();
        FirstScanCommand.RaiseCanExecuteChanged();
        NextScanCommand.RaiseCanExecuteChanged();
        UndoScanCommand.RaiseCanExecuteChanged();
        NewScanCommand.RaiseCanExecuteChanged();
        CancelScanCommand.RaiseCanExecuteChanged();
        DetachCommand.RaiseCanExecuteChanged();
        SaveTableCommand.RaiseCanExecuteChanged();
        ExportTableCommand.RaiseCanExecuteChanged();
        ClearCheatsCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ attach

    public void Attach(ProcessCandidate candidate)
    {
        var opened = TargetProcess.Open(candidate.Pid, out string error);
        if (opened is null)
        {
            Notify(error, NoticeKind.Error);
            return;
        }

        Detach();

        Process = opened;
        Session = new ScanSession(opened, _scanSettings);
        _freezer.Attach(opened);

        ProcessTitle = opened.Name;
        ProcessSubtitle = $"pid {opened.Pid} · {(opened.Is64Bit ? "64-bit" : "32-bit")}" +
                          (opened.CanWrite ? string.Empty : " · read-only");

        _game = string.IsNullOrEmpty(opened.ImagePath)
            ? GameFingerprint.Unknown(opened.Name)
            : GameFingerprint.For(opened.ImagePath);
        GameName = _game.DisplayName;

        LoadTableForGame();

        Raise(nameof(IsAttached));
        RaiseCommands();

        Status = $"Attached to {opened.Name}.";
        if (!opened.CanWrite)
            Notify("Opened read-only — values can be found but not changed. Restart as administrator to edit.", NoticeKind.Warning);
        else
            Notify($"Attached to {opened.Name}.", NoticeKind.Success);
    }

    public void Detach()
    {
        FlushPendingSave();
        _freezer.Attach(null);
        _freezer.Update([]);
        Session = null;
        Process?.Dispose();
        Process = null;
        Results = null;
        Chips.Clear();
        Cheats.Clear();
        _table = new CheatTable();
        _game = null;
        GameName = string.Empty;
        Verdict = string.Empty;
        VerdictDetail = string.Empty;
        ProcessTitle = "No process attached";
        ProcessSubtitle = "Click to choose a target";
        Raise(nameof(IsAttached));
        RaiseCommands();
    }

    private void LoadTableForGame()
    {
        Cheats.Clear();
        if (_game is null) return;

        _table = Settings.AutoLoadTables ? _library.LoadOrCreate(_game) : CheatTable.ForGame(_game);
        foreach (var entry in _table.Entries) Cheats.Add(new CheatRow(entry, this));

        PushFreezeSet();

        if (_table.Entries.Count > 0)
        {
            Notify($"Loaded {_table.Entries.Count} saved cheat(s) for {_table.GameName}.", NoticeKind.Success);
        }
        else
        {
            var others = _library.FindOtherVersions(_game);
            if (others.Count > 0)
                Notify($"No table for this build, but {others.Count} saved for another version of {_game.ExecutableName}. Import it from the table menu.", NoticeKind.Info);
        }
    }

    // ------------------------------------------------------------------ scanning

    private async Task RunScanAsync(bool first)
    {
        if (Session is null || Process is null) return;

        var request = new ScanRequest
        {
            Compare = SelectedCompare.Kind,
            Value = UserValue.Parse(ValueText),
            Value2 = UserValue.Parse(Value2Text),
            Profile = SelectedProfile.Profile,
            ForcedType = SelectedTypeOption.Type,
            Rounding = SelectedRounding.Mode,
            RestrictToInterpretations = PinnedInterpretations()
        };

        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;

        IsScanning = true;
        ScanProgress = 0;
        ScanPhase = "Preparing";

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgress = p.Fraction * 100;
            ScanPhase = $"{p.Phase} · {ByteSize.Format(p.BytesDone)} · {p.Found:N0} found";
        });

        var clock = Stopwatch.StartNew();
        try
        {
            var session = Session;
            var results = await Task.Run(() => first
                ? session.FirstScan(request, progress, token)
                : session.NextScan(request, progress, token), token);

            clock.Stop();
            ApplyResults(results, first, clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            Notify("Scan cancelled.", NoticeKind.Info);
        }
        catch (ScanException ex)
        {
            Notify(ex.Message, NoticeKind.Warning);
        }
        catch (Exception ex)
        {
            Notify("Scan failed: " + ex.Message, NoticeKind.Error);
        }
        finally
        {
            IsScanning = false;
            ScanProgress = 0;
            ScanPhase = string.Empty;
        }
    }

    private int[]? PinnedInterpretations()
    {
        var pinned = Chips.Where(c => c.IsPinned).Select(c => c.InterpId).ToArray();
        return pinned.Length == 0 ? null : pinned;
    }

    private void ApplyResults(ScanResults results, bool first, TimeSpan elapsed)
    {
        Results = new LazyResultList(results);
        RebuildChips(results);

        string speed = results.BytesScanned > 0 && first
            ? $" · {ByteSize.Format(results.BytesScanned)} in {elapsed.TotalSeconds:0.00}s"
            : $" · {elapsed.TotalMilliseconds:N0} ms";

        Status = $"{results.Count:N0} result(s){speed}";
        Raise(nameof(ResultCountText));
        BuildVerdict(results);
    }

    private void RebuildChips(ScanResults results)
    {
        var pinned = new HashSet<int>(Chips.Where(c => c.IsPinned).Select(c => c.InterpId));
        Chips.Clear();

        var best = results.BestGuess;
        foreach (var group in results.RankedGroups)
        {
            var chip = new InterpretationChip(group, best is not null && group.InterpId == best.InterpId)
            {
                IsPinned = pinned.Contains(group.InterpId)
            };
            Chips.Add(chip);
        }
    }

    private void BuildVerdict(ScanResults results)
    {
        if (results.Count == 0)
        {
            Verdict = "Nothing matched.";
            VerdictDetail = SelectedProfile.Profile == ScanProfile.Thorough
                ? "Try changing the value in the game and scanning for the new number, or turn off 'Writable memory only' in scan options."
                : "Try the Thorough profile, set Rounding to Loose, or turn off 'Writable memory only' in scan options.";
            return;
        }

        var usable = results.Groups.Where(g => !g.Capped).ToList();
        var best = results.BestGuess;

        if (usable.Count == 1 && best is not null)
        {
            Verdict = $"Stored as {best.Label}.";
            VerdictDetail = best.Count == 1
                ? "One address left — add it to the cheat table."
                : $"{best.Count:N0} addresses match. Change the value in the game, then scan again to narrow it down.";
            return;
        }

        if (best is null)
        {
            Verdict = "Too many matches to tell.";
            VerdictDetail = "Every encoding matched more addresses than is useful. Search a more unusual number, or change the value and scan again.";
            return;
        }

        Verdict = $"Best match: {best.Label} ({best.Count:N0}).";
        VerdictDetail = $"{usable.Count - 1} other encoding(s) still possible. Change the value in the game and scan again — the wrong ones will drop out.";
    }

    private void PinInterpretation(InterpretationChip? chip)
    {
        if (chip is null) return;
        chip.IsPinned = !chip.IsPinned;
        Status = chip.IsPinned
            ? $"Later scans will only consider {chip.Label}."
            : "Every surviving encoding will be considered again.";
    }

    private void KeepOnly(InterpretationChip? chip)
    {
        if (chip is null || Session?.Current is null) return;

        var filtered = Session.Current.FilterTo([chip.InterpId]);
        Session.Replace(filtered);
        ApplyResults(filtered, first: false, TimeSpan.Zero);
        Status = $"Kept only {chip.Label} — {filtered.Count:N0} result(s).";
    }

    private void UndoScan()
    {
        if (Session is null || !Session.CanUndo) return;
        Session.Undo();
        if (Session.Current is null) return;
        ApplyResults(Session.Current, first: false, TimeSpan.Zero);
        Status = "Went back one scan.";
    }

    private void NewScan()
    {
        Session?.Reset();
        Results = null;
        Chips.Clear();
        Verdict = string.Empty;
        VerdictDetail = string.Empty;
        Status = "Ready for a new search.";
        Raise(nameof(ScanButtonLabel));
    }

    public void SetVisibleRange(int first, int last)
    {
        _visibleFirst = first;
        _visibleLast = last;
    }

    // ------------------------------------------------------------------ cheat table

    public void AddResult(ResultRow row)
    {
        if (Process is null) return;

        var entry = new CheatEntry
        {
            Description = SuggestDescription(),
            Address = AddressSpec.ForAddress(Process, row.Address),
            FreezeValue = row.ValueText
        };
        entry.SetInterpretation(row.Interpretation);

        _table.Entries.Add(entry);
        Cheats.Add(new CheatRow(entry, this));
        CheatsChanged();
        Notify($"Added {entry.Address.Display} to the cheat table.", NoticeKind.Success);
    }

    private string SuggestDescription()
    {
        string basis = string.IsNullOrWhiteSpace(ValueText) ? "Value" : $"Value {ValueText}";
        int n = Cheats.Count + 1;
        return $"{basis} #{n}";
    }

    private void AddSelectedResults(object? parameter)
    {
        if (parameter is not IList selection || selection.Count == 0)
        {
            Notify("Select one or more results first.", NoticeKind.Info);
            return;
        }

        int added = 0;
        foreach (object? item in selection)
        {
            if (item is ResultRow row)
            {
                AddResult(row);
                added++;
            }
            if (added >= 200) break;
        }
    }

    private void RemoveCheats(object? parameter)
    {
        var doomed = new List<CheatRow>();
        if (parameter is IList selection)
        {
            foreach (object? item in selection)
            {
                if (item is CheatRow row) doomed.Add(row);
            }
        }

        if (doomed.Count == 0) return;

        foreach (var row in doomed)
        {
            Cheats.Remove(row);
            _table.Entries.Remove(row.Entry);
        }
        CheatsChanged();
        Status = $"Removed {doomed.Count} entr{(doomed.Count == 1 ? "y" : "ies")}.";
    }

    private void ClearCheats()
    {
        if (Cheats.Count == 0) return;
        var answer = MessageBox.Show(
            $"Remove all {Cheats.Count} entries from this table?",
            "Cheatmaster", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        Cheats.Clear();
        _table.Entries.Clear();
        CheatsChanged();
    }

    private void FreezeSelected(IList? selection)
    {
        if (selection is null) return;
        bool anyUnfrozen = selection.OfType<CheatRow>().Any(r => !r.Frozen);
        foreach (var row in selection.OfType<CheatRow>()) row.Frozen = anyUnfrozen;
    }

    public void CheatsChanged()
    {
        PushFreezeSet();
        Raise(nameof(FreezeStatus));
        ClearCheatsCommand.RaiseCanExecuteChanged();
        ExportTableCommand.RaiseCanExecuteChanged();
        RequestSave();
    }

    private void PushFreezeSet() => _freezer.Update(_table.Entries);

    private void RequestSave()
    {
        if (!Settings.AutoSaveTables || _game is null) return;
        _savePending = true;
        _lastSaveRequest = DateTime.UtcNow;
    }

    private void FlushPendingSave()
    {
        if (!_savePending || _game is null) return;
        _savePending = false;
        try
        {
            _library.Save(_table, _game);
        }
        catch (Exception ex)
        {
            Notify("Could not save the cheat table: " + ex.Message, NoticeKind.Error);
        }
    }

    private void SaveTableNow()
    {
        if (_game is null) return;
        _savePending = true;
        FlushPendingSave();
        Notify($"Saved {_table.Entries.Count} cheat(s) for {_table.GameName}.", NoticeKind.Success);
    }

    private void ExportTable()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export cheat table",
            Filter = "Cheatmaster table (*.cmt)|*.cmt",
            FileName = (string.IsNullOrWhiteSpace(_table.GameName) ? "cheats" : _table.GameName) + CheatTable.FileExtension
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _table.Save(dialog.FileName);
            Notify("Exported to " + Path.GetFileName(dialog.FileName), NoticeKind.Success);
        }
        catch (Exception ex)
        {
            Notify("Export failed: " + ex.Message, NoticeKind.Error);
        }
    }

    private void ImportTable()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import cheat table",
            Filter = "Cheatmaster table (*.cmt)|*.cmt|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var loaded = CheatTable.Load(dialog.FileName);
        if (loaded is null)
        {
            Notify("That file is not a readable cheat table.", NoticeKind.Error);
            return;
        }

        foreach (var entry in loaded.Entries)
        {
            var copy = entry.Clone();
            _table.Entries.Add(copy);
            Cheats.Add(new CheatRow(copy, this));
        }

        CheatsChanged();
        Notify($"Imported {loaded.Entries.Count} entr{(loaded.Entries.Count == 1 ? "y" : "ies")}.", NoticeKind.Success);
    }

    // ------------------------------------------------------------------ misc

    public void ClearNotice() => Notice = string.Empty;

    public void Notify(string message, NoticeKind kind = NoticeKind.Info)
    {
        Notice = message;
        NoticeLevel = kind;
    }

    private void RestartElevated()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return;

            Process?.Dispose();
            var info = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" };
            System.Diagnostics.Process.Start(info);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Notify("Could not restart with administrator rights: " + ex.Message, NoticeKind.Warning);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _tick++;

        if (_savePending && (DateTime.UtcNow - _lastSaveRequest).TotalMilliseconds > 1200)
            FlushPendingSave();

        var process = Process;
        if (process is null) return;

        if (_tick % 8 == 0 && !process.IsRunning)
        {
            string name = process.Name;
            Detach();
            Notify($"{name} exited.", NoticeKind.Warning);
            return;
        }

        if (!Settings.LiveValues || IsScanning) return;

        foreach (var row in Cheats) row.Refresh(process);

        if (Results is not null)
        {
            foreach (var row in Results.Realized(_visibleFirst - 4, _visibleLast + 4))
                row.Refresh(process);
        }

        Raise(nameof(FreezeStatus));
    }

    public void Dispose()
    {
        _timer.Stop();
        FlushPendingSave();
        _freezer.Dispose();
        _scanCancellation?.Dispose();
        Process?.Dispose();
        Settings.Save();
    }
}
