using System.Collections.ObjectModel;
using System.Globalization;
using Cheatmaster.App.Infrastructure;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.ViewModels;

public sealed class PointerPathRow
{
    public PointerPathRow(PointerPath path) => Path = path;

    public PointerPath Path { get; }
    public string Display => Path.Display;
    public string DepthText => Path.Depth == 1 ? "1 hop" : $"{Path.Depth} hops";
}

/// <summary>
/// Turns an address that moves into a route that does not.
///
/// A value on the heap sits somewhere new every launch, so a saved address is worthless the next
/// day. This walks backwards from the address — what points here, and what points at that — until
/// the trail reaches a fixed position inside a module, and offers the routes it found.
/// </summary>
public sealed class PointerScanViewModel : ObservableObject
{
    private readonly TargetProcess _process;
    private CancellationTokenSource? _cancellation;

    private PointerPathRow? _selected;
    private string _status = string.Empty;
    private double _progress;
    private bool _isBusy;
    private bool _hasScanned;

    public PointerScanViewModel(TargetProcess process, ulong target, string description, ScanType type, ulong expectedBits)
    {
        _process = process;
        Target = target;
        Description = description;
        Type = type;
        ExpectedBits = expectedBits;

        ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy);

        Status = "This address is on the heap, so it will be somewhere else next time the game starts. " +
                 "Searching for a route that starts from a fixed point in the program.";
    }

    public ulong Target { get; }
    public string Description { get; }
    public ScanType Type { get; }
    public ulong ExpectedBits { get; }

    public string TargetText => Target.ToString("X", CultureInfo.InvariantCulture);

    public ObservableCollection<PointerPathRow> Paths { get; } = [];

    public RelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>How far into a structure a field may sit.</summary>
    public int MaxOffset { get; set; } = 0x800;

    /// <summary>How many pointers deep to follow. Deeper finds more routes and takes longer.</summary>
    public int MaxDepth { get; set; } = 4;

    public IReadOnlyList<int> DepthOptions { get; } = [2, 3, 4, 5, 6];
    public IReadOnlyList<int> OffsetOptions { get; } = [0x100, 0x400, 0x800, 0x1000, 0x2000];

    public PointerPathRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) Raise(nameof(HasSelection));
        }
    }

    public bool HasSelection => Selected is not null;

    public bool HasScanned
    {
        get => _hasScanned;
        private set
        {
            if (Set(ref _hasScanned, value)) Raise(nameof(FoundNothing));
        }
    }

    public bool FoundNothing => HasScanned && Paths.Count == 0;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            ScanCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task ScanAsync()
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        IsBusy = true;
        Paths.Clear();
        HasScanned = false;

        var progress = new Progress<ScanProgress>(p =>
        {
            Progress = p.Fraction * 100;
            Status = $"{p.Phase} · {p.Found:N0} route(s) so far";
        });

        int depth = MaxDepth;
        int offset = MaxOffset;
        var process = _process;
        ulong target = Target;

        try
        {
            // Indexing every pointer in the target is heavy work; none of it belongs on the UI thread.
            var results = await Task.Run(() =>
            {
                var map = PointerMap.Build(process, RegionFilter.Everything, progress: progress, ct: token);
                var paths = PointerScanner.Find(process, map, target,
                    new PointerScanOptions { MaxDepth = depth, MaxOffset = offset }, progress, token);

                // Only keep routes that read back the value we started from.
                return PointerScanner.Verify(process, paths, Type, ExpectedBits, token);
            }, token);

            foreach (var path in results) Paths.Add(new PointerPathRow(path));
            Selected = Paths.FirstOrDefault();
            HasScanned = true;

            Status = Paths.Count == 0
                ? "No route found. Try a greater depth or a larger structure size, or pick a different address from the group."
                : $"{Paths.Count} route(s) reach this value today. Restart the game and re-check to see which ones still hold.";
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled.";
        }
        catch (ScanException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            Status = "The search failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }
}
