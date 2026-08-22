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
        AttachCommand = new RelayCommand(() => AttachRequested?.Invoke(), () => !IsScanning);
        DetachCommand = new RelayCommand(Detach, () => IsAttached && !IsScanning);
        ScanCommand = new RelayCommand(async () => await RunScanAsync(first: !HasResults), () => CanScan);
        FirstScanCommand = new RelayCommand(async () => await RunScanAsync(first: !HasResults), () => CanScan);
        NextScanCommand = new RelayCommand(async () => await RunScanAsync(first: false), () => CanScan && HasResults);
        UndoScanCommand = new RelayCommand(UndoScan, () => Session?.CanUndo == true && !IsScanning);
        NewScanCommand = new RelayCommand(NewScan, () => HasResults && !IsScanning);
        CancelScanCommand = new RelayCommand(() => _scanCancellation?.Cancel(), () => IsScanning);
        CaptureCommand = new RelayCommand(async () => await CaptureAsync(), () => IsAttached && !IsScanning);
        ClearSnapshotCommand = new RelayCommand(ClearSnapshot, () => HasSnapshot);
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
        SetValueCommand = new RelayCommand(o => SetValueForSelected(o, alsoFreeze: false));
        FreezeAtValueCommand = new RelayCommand(o => SetValueForSelected(o, alsoFreeze: true));
        GroupSelectedCommand = new RelayCommand(GroupSelected);
        RecheckRoutesCommand = new RelayCommand(RecheckRoutes, () => IsAttached && !IsScanning);
        ShowScannerCommand = new RelayCommand(() => IsLibraryView = false);
        ShowLibraryCommand = new RelayCommand(() => IsLibraryView = true);

        Library = new LibraryViewModel(_library) { FetchArtworkEnabled = Settings.FetchArtwork };
        Library.OpenRequested += OnLibraryOpenRequested;

        SelectedProfile = Profiles.FirstOrDefault(p => p.Profile == Settings.Profile) ?? Profiles[1];
        SelectedRounding = RoundingModes.FirstOrDefault(r => r.Mode == Settings.Rounding) ?? RoundingModes[1];
        SelectedAlignment = Alignments.Contains(Settings.Alignment) ? Settings.Alignment : 4;
        SelectedTypeOption = TypeOptions[0];
        SelectedCompare = CompareOptions[0];

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _timer.Tick += OnTick;
        _timer.Start();


        Guide = new GuideViewModel(this);
        ShowGuideCommand = new RelayCommand(() => Guide.Show());

        // The walkthrough opens by itself the first time, which is the whole point of it, and
        // never again once it has been finished or skipped.
        if (!Settings.GuideDismissed) Guide.Show();

        Status = "Not attached. Pick a process to begin.";
    }

    public event Action? AttachRequested;

    public LibraryViewModel Library { get; }

    public RelayCommand ShowScannerCommand { get; }
    public RelayCommand ShowLibraryCommand { get; }

    private bool _isLibraryView;

    /// <summary>Which of the two halves of the app is on screen: the scanner or the saved cheats.</summary>
    public bool IsLibraryView
    {
        get => _isLibraryView;
        set
        {
            if (!Set(ref _isLibraryView, value)) return;
            Raise(nameof(IsScannerView));
            if (value) _ = Library.ReloadAsync();
        }
    }

    public bool IsScannerView => !_isLibraryView;

    private void OnLibraryOpenRequested(LibraryGameRow row)
    {
        IsLibraryView = false;

        if (IsAttached && string.Equals(Process!.Name, row.ExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            Notify($"{row.Name} is already attached — its cheats are in the table below.", NoticeKind.Info);
            return;
        }

        Notify($"Start {row.Name}, then attach to {row.ExecutableName}. Its saved cheats load automatically.", NoticeKind.Info);
        AttachRequested?.Invoke();
    }

    /// <summary>
    /// Looks up cover art and a description for the attached game so it is already dressed when
    /// the user opens the library.
    /// </summary>
    private async Task FetchGameMetadataAsync()
    {
        if (!Settings.FetchArtwork || _game is null || _table.MetadataFetched) return;

        var fingerprint = _game;
        var table = _table;

        try
        {
            var metadata = await new GameMetadataService(null, Services.CoverOptimizer.Shrink).FetchAsync(fingerprint);
            if (table != _table) return;   // attached elsewhere while we were waiting

            table.MetadataFetched = true;
            if (metadata is not null)
            {
                if (!string.IsNullOrWhiteSpace(metadata.Name)) table.GameName = metadata.Name;
                table.Description = metadata.Description;
                table.Developer = metadata.Developer;
                table.ReleaseDate = metadata.ReleaseDate;
                table.SteamAppId = metadata.SteamAppId;
                table.GogProductId = metadata.GogProductId;
                table.ArtPath = metadata.ArtPath;
                GameName = table.GameName;
            }

            RequestSave();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Artwork is a nicety; never let it interrupt the work.
            Debug.WriteLine("metadata lookup failed: " + ex.Message);
        }
    }

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
        new("Between", CompareKind.Between),
        new("Was … now … (finds hidden values)", CompareKind.ChangedFromTo),
        new("Changed", CompareKind.Changed),
        new("Unchanged", CompareKind.Unchanged),
        new("Increased", CompareKind.Increased),
        new("Decreased", CompareKind.Decreased),
        new("Increased by", CompareKind.IncreasedBy),
        new("Decreased by", CompareKind.DecreasedBy)
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
            Raise(nameof(NeedsSnapshot));
            Raise(nameof(ValuePlaceholder));
            Raise(nameof(Value2Placeholder));
            RaiseCommands();
        }
    }

    public bool NeedsValue => SelectedCompare?.Kind.NeedsValue() ?? true;
    public bool NeedsSecondValue => SelectedCompare?.Kind.NeedsSecondValue() ?? false;

    /// <summary>True for comparisons that work off a captured copy of memory rather than a typed value.</summary>
    public bool NeedsSnapshot => SelectedCompare is not null &&
        (SelectedCompare.Kind == CompareKind.ChangedFromTo ||
         (SelectedCompare.Kind.NeedsPrevious() && !HasResults));

    public string ValuePlaceholder => SelectedCompare?.Kind switch
    {
        CompareKind.ChangedFromTo => "value before the change",
        CompareKind.IncreasedBy or CompareKind.DecreasedBy => "by how much",
        _ => "value you can see in game"
    };

    public string Value2Placeholder => SelectedCompare?.Kind switch
    {
        CompareKind.ChangedFromTo => "value now",
        _ => "and this"
    };

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

    public string VersionText =>
        "Cheatmaster " + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.4");

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
    public RelayCommand CaptureCommand { get; }
    public RelayCommand ClearSnapshotCommand { get; }
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
    public RelayCommand SetValueCommand { get; }
    public RelayCommand FreezeAtValueCommand { get; }
    public RelayCommand GroupSelectedCommand { get; }

    /// <summary>Follows every saved route again, which is the only thing that proves one.</summary>
    public RelayCommand RecheckRoutesCommand { get; }

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
        CaptureCommand.RaiseCanExecuteChanged();
        ClearSnapshotCommand.RaiseCanExecuteChanged();
        AttachCommand.RaiseCanExecuteChanged();
        DetachCommand.RaiseCanExecuteChanged();
        SaveTableCommand.RaiseCanExecuteChanged();
        ExportTableCommand.RaiseCanExecuteChanged();
        ClearCheatsCommand.RaiseCanExecuteChanged();
        RecheckRoutesCommand.RaiseCanExecuteChanged();
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

    /// <summary>Suspends the live-value refresh, which would otherwise overwrite a cell mid-edit.</summary>
    public bool IsEditingCell { get; set; }

    /// <summary>Raised when the cheat list changes, so global hotkeys can be re-registered.</summary>
    public event Action? CheatSetChanged;

    public void Detach()
    {
        // A scan still reading this process must stop before the handle closes under it.
        _scanCancellation?.Cancel();
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
        _ = FetchGameMetadataAsync();

        if (_table.Entries.Count > 0)
        {
            string message = $"Loaded {_table.Entries.Count} saved cheat(s) for {_table.GameName}.";

            // The game has just started, which is exactly when a saved route either still works or
            // does not. There is no better moment to ask.
            int routes = _table.Entries.Count(static entry => entry.Address.IsPointerChain);
            if (routes > 0)
                message += $" {routes} of them find their address through a route — re-check those now the game has restarted.";

            Notify(message, NoticeKind.Success);
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
            var results = await Task.Run(() => RunScanCore(session, request, first, progress, token), token);

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

    public bool HasSnapshot => Session?.HasSnapshot == true;

    public string SnapshotStatus
    {
        get
        {
            var snapshot = Session?.Snapshot;
            if (snapshot is null) return string.Empty;

            int seconds = (int)(DateTimeOffset.Now - snapshot.TakenAt).TotalSeconds;
            string age = seconds < 60 ? $"{seconds}s ago" : $"{seconds / 60}m ago";
            return $"{ByteSize.Format(snapshot.TotalBytes)} captured {age}";
        }
    }

    /// <summary>
    /// Takes the copy of memory that change-based and obfuscated-value searches compare against.
    /// </summary>
    private async Task CaptureAsync()
    {
        if (Session is null) return;

        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;

        IsScanning = true;
        ScanPhase = "Capturing";
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgress = p.Fraction * 100;
            ScanPhase = $"Capturing · {ByteSize.Format(p.BytesDone)}";
        });

        try
        {
            var session = Session;
            var value = UserValue.Parse(ValueText);
            var snapshot = await Task.Run(() => session.CaptureSnapshot(value, progress, token), token);

            Notify($"Captured {ByteSize.Format(snapshot.TotalBytes)} of memory. " +
                   "Now change the value in the game, then scan.", NoticeKind.Success);
            Status = $"Snapshot: {ByteSize.Format(snapshot.TotalBytes)} across {snapshot.RegionCount:N0} regions " +
                     $"in {snapshot.Duration.TotalSeconds:0.00}s";
        }
        catch (OperationCanceledException)
        {
            Notify("Capture cancelled.", NoticeKind.Info);
        }
        catch (ScanException ex)
        {
            Notify(ex.Message, NoticeKind.Warning);
        }
        catch (Exception ex)
        {
            Notify("Capture failed: " + ex.Message, NoticeKind.Error);
        }
        finally
        {
            IsScanning = false;
            ScanProgress = 0;
            ScanPhase = string.Empty;
            Raise(nameof(HasSnapshot));
            Raise(nameof(SnapshotStatus));
            Raise(nameof(NeedsSnapshot));
            RaiseCommands();
        }
    }

    private void ClearSnapshot()
    {
        Session?.ClearSnapshot();
        Raise(nameof(HasSnapshot));
        Raise(nameof(SnapshotStatus));
        Raise(nameof(NeedsSnapshot));
        RaiseCommands();
        Status = "Snapshot discarded.";
    }

    /// <summary>
    /// Picks the right search for the comparison. A change-based comparison narrows existing
    /// results when there are any, and otherwise compares against the captured snapshot, which
    /// is what makes searching without knowing the value work at all.
    /// </summary>
    private static ScanResults RunScanCore(ScanSession session, ScanRequest request, bool first,
        IProgress<ScanProgress> progress, CancellationToken token)
    {
        if (request.Compare == CompareKind.ChangedFromTo)
            return session.DifferentialScan(request.Value, request.Value2, request, progress, token);

        if (request.Compare.NeedsPrevious())
        {
            if (session.HasResults) return session.NextScan(request, progress, token);
            if (session.HasSnapshot) return session.SnapshotScan(request, progress, token);
            throw new ScanException("Capture memory first, change the value in the game, then scan.");
        }

        return first ? session.FirstScan(request, progress, token) : session.NextScan(request, progress, token);
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

        // Undoing past the first scan must clear the grid too, or the results on screen no
        // longer match what the session thinks it holds.
        if (Session.Current is null)
        {
            NewScan();
            Status = "Back to the start.";
            return;
        }

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


    /// <summary>True when anything in the table finds its address through a route.</summary>
    public bool HasRoutes
    {
        get
        {
            foreach (var row in Cheats)
            {
                if (row.IsRoute) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Re-resolves every saved route and reports which ones still reach a value.
    ///
    /// This is the half of pointer scanning that was missing. A route found today always works
    /// today — the only thing that proves it is following it again after the game has been
    /// restarted, which is why this exists as a step of its own rather than as part of the search.
    /// </summary>
    private void RecheckRoutes()
    {
        if (Process is not { } process)
        {
            Notify("Attach to the game first — a route can only be checked against a running game.", NoticeKind.Warning);
            return;
        }

        int held = 0, lost = 0;
        foreach (var row in Cheats)
        {
            if (!row.IsRoute) continue;

            if (row.Entry.TryReadValue(process, out _))
            {
                row.Entry.LastVerified = DateTimeOffset.Now;
                held++;
            }
            else
            {
                row.Entry.LastVerified = null;
                lost++;
            }

            row.RaiseRouteChanged();
        }

        if (held + lost == 0)
        {
            Notify("Nothing in this table finds its address through a route.", NoticeKind.Info);
            return;
        }

        CheatsChanged();

        // "Reaches a value" is all this can honestly claim: a route that lands on the wrong object
        // still reads something, and only the value on screen can tell you that.
        Notify(lost == 0
            ? $"All {held} route(s) still reach a value. Check one against the game to be sure it is the right one."
            : $"{held} route(s) still reach a value, {lost} no longer resolve. Trace those again from a fresh search.",
            lost == 0 ? NoticeKind.Success : NoticeKind.Warning);
    }

    // ------------------------------------------------------------------ the guided walkthrough

    /// <summary>
    /// The first-run walkthrough. It drives the commands below rather than carrying its own copy
    /// of the search, so what it does and what the buttons do cannot drift apart.
    /// </summary>
    public GuideViewModel Guide { get; }

    public RelayCommand ShowGuideCommand { get; }

    /// <summary>Chooses a comparison by kind, so the guide need not know the order of the list.</summary>
    public void SelectCompare(CompareKind kind)
    {
        foreach (var option in CompareOptions)
        {
            if (option.Kind != kind) continue;
            SelectedCompare = option;
            return;
        }
    }

    /// <summary>The same scan the search button runs, awaited so the guide can react to the result.</summary>
    public Task RunScanFromGuideAsync() => RunScanAsync(first: !HasResults);

    public Task CaptureSnapshotAsync() => CaptureAsync();

    /// <summary>Throws the current results away and starts over, as the New search button does.</summary>
    public void StartOver() => NewScan();

    /// <summary>The first few results, for the guide to save in one go.</summary>
    public List<ResultRow> TakeResults(int max)
    {
        var rows = new List<ResultRow>();
        if (Results is not { } results) return rows;

        int count = Math.Min(max, results.Count);
        for (int i = 0; i < count; i++) rows.Add(results[i]);
        return rows;
    }

    // ------------------------------------------------------------------ cheat table

    public void AddResult(ResultRow row) => AddResults([row]);

    /// <summary>
    /// Adds a result to the table and puts a route on it in one step. A route traced from a scan
    /// result has nowhere to live until the result becomes an entry.
    /// </summary>
    public void AddResultWithRoute(ResultRow row, PointerPath path)
    {
        int before = Cheats.Count;
        AddResult(row);
        if (Cheats.Count == before) return;

        ApplyPointerPath(Cheats[^1], path);
    }

    /// <summary>
    /// Adds results in one batch: the freeze set is rebuilt once and one notice is shown, rather
    /// than once per row, which a two-hundred-row selection would otherwise turn into a stall.
    /// </summary>
    public void AddResults(IReadOnlyList<ResultRow> rows, string? name = null)
    {
        if (Process is null || rows.Count == 0) return;

        const int Limit = 200;
        int added = 0;
        string? firstAddress = null;

        // Several candidates added at once are almost always one thing the player can see, and
        // there is usually no way to tell which of them matters. They get one name up front so
        // the library can show them as a single line instead of a wall of addresses.
        // The guide has already asked what this is, so it hands the name over rather than asking twice.
        string group = name?.Trim() ?? string.Empty;
        if (rows.Count > 1 && group.Length == 0)
        {
            string suggestion = string.IsNullOrWhiteSpace(ValueText) ? "New cheat" : $"Value {ValueText}";
            group = (PromptForValue?.Invoke(
                "Name this cheat",
                $"{Math.Min(rows.Count, Limit)} addresses will be kept together under one name:",
                suggestion) ?? suggestion).Trim();

            if (group.Length == 0) group = suggestion;
        }

        foreach (var row in rows)
        {
            if (added >= Limit) break;

            var entry = new CheatEntry
            {
                Description = SuggestDescription(),
                Address = AddressSpec.ForAddress(Process, row.Address),
                FreezeValue = row.ValueText,
                Group = group
            };
            entry.SetInterpretation(row.Interpretation);

            _table.Entries.Add(entry);
            Cheats.Add(new CheatRow(entry, this));
            firstAddress ??= entry.Address.Display;
            added++;
        }

        if (added == 0) return;
        CheatsChanged();

        string message = added == 1
            ? $"Added {firstAddress} to the cheat table."
            : $"Added {added} addresses to the cheat table.";
        if (rows.Count > Limit) message += $" {rows.Count - Limit} more were left out — {Limit} at a time is the cap.";
        Notify(message, NoticeKind.Success);
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

        var rows = new List<ResultRow>(selection.Count);
        foreach (object? item in selection)
        {
            if (item is ResultRow row) rows.Add(row);
        }

        AddResults(rows);
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

    /// <summary>
    /// Set from the view so bulk edits can ask for a value. A callback rather than a window
    /// reference, so the view model never opens a window itself.
    /// </summary>
    public Func<string, string, string, string?>? PromptForValue { get; set; }

    /// <summary>Every entry held. Reads false while any one of them is loose.</summary>
    public bool FreezeAll
    {
        get => Cheats.Count > 0 && Cheats.All(static c => c.Frozen);
        set
        {
            if (Cheats.Count == 0) return;
            foreach (var row in Cheats) row.SetFrozen(value, notifyHost: false);
            CheatsChanged();
            Status = value
                ? $"Holding all {Cheats.Count} value{(Cheats.Count == 1 ? "" : "s")}."
                : "Released every value.";
        }
    }

    private void FreezeSelected(IList? selection)
    {
        var rows = Rows(selection);
        if (rows.Count == 0)
        {
            Notify("Select one or more entries first.", NoticeKind.Info);
            return;
        }

        // Mixed selection freezes; an all-frozen selection releases.
        bool freeze = rows.Any(static r => !r.Frozen);
        foreach (var row in rows) row.SetFrozen(freeze, notifyHost: false);
        CheatsChanged();

        Status = freeze
            ? $"Holding {rows.Count} value{(rows.Count == 1 ? "" : "s")}."
            : $"Released {rows.Count} value{(rows.Count == 1 ? "" : "s")}.";
    }

    /// <summary>Writes one value into every selected entry, optionally holding them there.</summary>
    private void SetValueForSelected(object? parameter, bool alsoFreeze)
    {
        var rows = Rows(parameter as IList);
        if (rows.Count == 0)
        {
            Notify("Select one or more entries first.", NoticeKind.Info);
            return;
        }

        if (Process is null)
        {
            Notify("Attach to a process before editing values.", NoticeKind.Warning);
            return;
        }

        string message = rows.Count == 1
            ? $"Write this value to {rows[0].Description}:"
            : $"Write this value to all {rows.Count} selected entries:";

        string? input = PromptForValue?.Invoke(alsoFreeze ? "Freeze at value" : "Set value", message, rows[0].ValueText);
        if (string.IsNullOrWhiteSpace(input)) return;

        int written = 0;
        foreach (var row in rows)
        {
            if (!row.TrySetValue(input)) continue;
            written++;
            if (alsoFreeze) row.SetFrozen(true, notifyHost: false);
        }

        CheatsChanged();

        if (written == 0)
            Notify("None of the selected addresses could be written.", NoticeKind.Error);
        else if (written < rows.Count)
            Notify($"Wrote {input} to {written} of {rows.Count} entries; the rest refused.", NoticeKind.Warning);
        else
            Notify($"Wrote {input} to {written} entr{(written == 1 ? "y" : "ies")}.", NoticeKind.Success);
    }

    /// <summary>
    /// Re-anchors an entry to a pointer route. The address it had was only good for this run of
    /// the game; the route is good for every run.
    /// </summary>
    public void ApplyPointerPath(CheatRow row, PointerPath path)
    {
        row.Entry.Address = path.ToAddressSpec();
        row.RaiseAddressChanged();
        CheatsChanged();
        Notify($"{row.Description} now finds its own address through {path.Display}, so it will still work after a restart.",
            NoticeKind.Success);
    }

    /// <summary>Files the selected entries under one name, so the library shows them as one line.</summary>
    private void GroupSelected(object? parameter)
    {
        var rows = Rows(parameter as IList);
        if (rows.Count == 0)
        {
            Notify("Select the entries you want to keep together.", NoticeKind.Info);
            return;
        }

        string suggestion = rows.Select(static r => r.Group).FirstOrDefault(static g => !string.IsNullOrWhiteSpace(g))
                            ?? (string.IsNullOrWhiteSpace(ValueText) ? "New cheat" : $"Value {ValueText}");

        string? name = PromptForValue?.Invoke(
            "Name this cheat",
            $"File {rows.Count} entr{(rows.Count == 1 ? "y" : "ies")} under:",
            suggestion);
        if (name is null) return;

        name = name.Trim();
        foreach (var row in rows) row.SetGroup(name, notifyHost: false);
        CheatsChanged();

        Status = name.Length == 0
            ? $"Took {rows.Count} entr{(rows.Count == 1 ? "y" : "ies")} out of their group."
            : $"Filed {rows.Count} entr{(rows.Count == 1 ? "y" : "ies")} under \"{name}\".";
    }

    private static List<CheatRow> Rows(IList? selection)
    {
        var rows = new List<CheatRow>();
        if (selection is null) return rows;

        foreach (object? item in selection)
        {
            if (item is CheatRow row) rows.Add(row);
        }
        return rows;
    }

    public void CheatsChanged()
    {
        PushFreezeSet();
        Raise(nameof(FreezeStatus));
        Raise(nameof(FreezeAll));
        Raise(nameof(HasRoutes));
        CheatSetChanged?.Invoke();
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
            Filter = "Cheat tables (*.cmt;*.CT)|*.cmt;*.CT|Cheatmaster table (*.cmt)|*.cmt"
                     + "|Cheat Engine table (*.CT)|*.CT|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        if (".CT".Equals(System.IO.Path.GetExtension(dialog.FileName), StringComparison.OrdinalIgnoreCase))
        {
            ImportCheatEngineTable(dialog.FileName);
            return;
        }

        var loaded = CheatTable.Load(dialog.FileName);
        if (loaded is null)
        {
            // A Cheat Engine table saved under another name still reads fine, so it is worth a try
            // before telling someone their file is no good.
            var fallback = CheatEngineTable.Load(dialog.FileName);
            if (!fallback.Failed)
            {
                Adopt(fallback);
                return;
            }

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

    /// <summary>Shows what a Cheat Engine table could not bring across, when anything could not.</summary>
    public Action<ImportReport>? ShowImportReport { get; set; }

    private void ImportCheatEngineTable(string path)
    {
        var report = CheatEngineTable.Load(path);
        if (report.Failed)
        {
            Notify(report.Error!, NoticeKind.Error);
            return;
        }

        Adopt(report);
    }

    private void Adopt(ImportReport report)
    {
        foreach (var entry in report.Entries)
        {
            _table.Entries.Add(entry);
            Cheats.Add(new CheatRow(entry, this));
        }

        if (report.Entries.Count > 0) CheatsChanged();

        Notify(report.Summary, report.Skipped.Count > 0 ? NoticeKind.Warning : NoticeKind.Success);

        // Never quietly: an entry that looks imported and does nothing is worse than one refused.
        if (report.Skipped.Count > 0) ShowImportReport?.Invoke(report);
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

        // Skip while a cell is being edited: pushing a fresh value would rewrite what is
        // being typed, keystroke by keystroke.
        if (!IsEditingCell)
        {
            foreach (var row in Cheats) row.Refresh(process);
        }

        if (Results is not null)
        {
            foreach (var row in Results.Realized(_visibleFirst - 4, _visibleLast + 4))
                row.Refresh(process);
        }

        Raise(nameof(FreezeStatus));
        Raise(nameof(FreezeAll));
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
