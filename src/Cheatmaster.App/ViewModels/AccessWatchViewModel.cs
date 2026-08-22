using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Threading;
using Cheatmaster.App.Infrastructure;
using Cheatmaster.Core.Debugging;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.ViewModels;

/// <summary>One instruction that touched the address, as a row in the list.</summary>
public sealed class AccessSiteRow : ObservableObject
{
    public AccessSiteRow(AccessSite site) => Site = site;

    public AccessSite Site { get; private set; }

    public ulong Key => Site.InstructionPointer;

    public string Location => Site.Location;

    public string CountText => Site.Count == 1 ? "1 time" : $"{Site.Count} times";

    public string WhatText
    {
        get
        {
            if (Site.Reads > 0 && Site.Writes > 0) return $"{Site.Writes} write(s), {Site.Reads} read(s)";
            return Site.Writes > 0 ? "writes it" : "reads it";
        }
    }

    public string BaseText => Site.Base is null ? "no base register" : Site.Base.Display;

    public bool HasBase => Site.Base is not null;

    public string ThreadText => $"thread {Site.Latest.ThreadId}";

    public void Update(AccessSite site)
    {
        Site = site;
        Raise(nameof(Site));
        Raise(nameof(CountText));
        Raise(nameof(WhatText));
        Raise(nameof(BaseText));
        Raise(nameof(HasBase));
    }
}

/// <summary>
/// Watches an address and shows which of the game's own instructions touch it.
///
/// A scan narrows a value down to a handful of addresses and then stops being able to help: there
/// is nothing in the numbers that says which one the game reads. Code touching an address settles
/// it, and the register state at that moment usually names the object the value belongs to, which
/// is the hard half of finding a route that survives a restart.
/// </summary>
public sealed class AccessWatchViewModel : ObservableObject, IDisposable
{
    private readonly TargetProcess _process;
    private readonly DispatcherTimer _timer;

    private AccessWatch? _watch;
    private AccessSiteRow? _selected;
    private string _status;
    private bool _isBusy;
    private bool _writesOnly;
    private int _hitCount;

    public AccessWatchViewModel(TargetProcess process, ulong address, string description, ScanType type)
    {
        _process = process;
        Address = address;
        Description = description;
        Type = type;

        StartCommand = new RelayCommand(async () => await StartAsync(), () => !IsWatching && !IsBusy);
        StopCommand = new RelayCommand(Stop, () => IsWatching);

        _status = "Nothing is being watched yet.";

        // Polling keeps every reading on the UI thread. The debug loop is busy freezing and
        // resuming the game and has no business dispatching into a window.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Poll();
    }

    public ulong Address { get; }

    public string Description { get; }

    public ScanType Type { get; }

    public string AddressText => Address.ToString("X", CultureInfo.InvariantCulture);

    public ObservableCollection<AccessSiteRow> Sites { get; } = [];

    public RelayCommand StartCommand { get; }

    public RelayCommand StopCommand { get; }

    /// <summary>
    /// Writes only is the quieter watch. Reads and writes finds the code that displays a value as
    /// well as the code that changes it, at the cost of stopping the game far more often.
    /// </summary>
    public bool WritesOnly
    {
        get => _writesOnly;
        set => Set(ref _writesOnly, value);
    }

    public bool IsWatching => _watch is { IsRunning: true };

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) RaiseCommands();
        }
    }

    public int HitCount
    {
        get => _hitCount;
        private set
        {
            if (Set(ref _hitCount, value)) Raise(nameof(HitCountText));
        }
    }

    public string HitCountText => _hitCount == 1 ? "1 access" : $"{_hitCount} accesses";

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool FoundNothing => Sites.Count == 0;

    public AccessSiteRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection));
            Raise(nameof(CanTraceRoute));
            Raise(nameof(RegisterText));
            Raise(nameof(CodeText));
            Raise(nameof(BaseExplanation));
        }
    }

    public bool HasSelection => Selected is not null;

    public bool CanTraceRoute => Selected?.Site.Base is not null;

    /// <summary>What the chosen base means, spelled out, because it is the point of the whole dialog.</summary>
    public string BaseExplanation
    {
        get
        {
            if (Selected is not { } row) return string.Empty;
            if (row.Site.Base is not { } guess)
            {
                return "No register held anything near this address, so the value is probably reached "
                       + "through a computed address rather than an object pointer.";
            }

            string where = guess.IsStack
                ? "That is the stack, so this is a local variable rather than a field of an object — "
                  + "it will not lead to a route that survives a restart."
                : $"So the object is at {guess.Value.ToString("X", CultureInfo.InvariantCulture)} and the value sits "
                  + $"{guess.Offset.ToString("X", CultureInfo.InvariantCulture)} bytes into it. A pointer search that "
                  + "already knows that has far less ground to cover.";

            return $"The instruction reached the value as {guess.Display}. {where}";
        }
    }

    public string RegisterText
    {
        get
        {
            if (Selected is not { } row) return string.Empty;

            var text = new StringBuilder();
            int column = 0;
            foreach (var register in row.Site.Latest.Registers)
            {
                text.Append(register.Name.PadRight(4))
                    .Append(' ')
                    .Append(register.Value.ToString("X16", CultureInfo.InvariantCulture));
                if (++column % 4 == 0) text.AppendLine();
                else text.Append("   ");
            }

            return text.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// The bytes around the instruction, for anyone who reads assembly. The pointer is left after
    /// the instruction that ran, so the bar marks where it stopped rather than where it started.
    /// </summary>
    public string CodeText
    {
        get
        {
            if (Selected is not { } row) return string.Empty;

            var hit = row.Site.Latest;
            if (hit.Code.Length == 0) return "The code around this instruction could not be read.";

            var text = new StringBuilder();
            text.Append(hit.CodeBase.ToString("X", CultureInfo.InvariantCulture)).Append("  ");

            for (int i = 0; i < hit.Code.Length; i++)
            {
                if (hit.CodeBase + (ulong)i == hit.InstructionPointer) text.Append("| ");
                text.Append(hit.Code[i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
            }

            return text.ToString().TrimEnd();
        }
    }

    private async Task StartAsync()
    {
        if (IsWatching) return;

        IsBusy = true;
        Sites.Clear();
        Selected = null;
        HitCount = 0;
        Raise(nameof(FoundNothing));
        Status = "Attaching a debugger to the game…";

        var process = _process;
        ulong address = Address;
        int width = Type.Width();
        var options = new AccessWatchOptions
        {
            On = WritesOnly ? WatchOn.Write : WatchOn.ReadOrWrite,
            MaxHits = 200
        };

        // Attaching is quick but it is still a wait on another process, and the window has to stay
        // answerable while it happens.
        var (watch, error) = await Task.Run(() =>
        {
            var started = AccessWatch.Start(process, address, width, options, out string why);
            return (started, why);
        });

        IsBusy = false;

        if (watch is null)
        {
            Status = error;
            return;
        }

        _watch = watch;
        Raise(nameof(IsWatching));
        RaiseCommands();

        Status = watch.CoversWholeValue
            ? "Watching. Play the game and make the value change."
            : "Watching. This value is spread awkwardly enough that only part of it can be covered, "
              + "so some accesses will be missed.";

        _timer.Start();
    }

    private void Poll()
    {
        if (_watch is not { } watch) return;

        HitCount = watch.HitCount;
        Merge(watch.Snapshot());

        if (watch.IsRunning) return;

        // It stopped on its own, having seen enough.
        _timer.Stop();
        Status = watch.Status;
        Raise(nameof(IsWatching));
        RaiseCommands();
    }

    /// <summary>
    /// Updates the rows in place instead of rebuilding them, so the list does not reorder itself
    /// under the cursor and the selection survives.
    /// </summary>
    private void Merge(List<AccessSite> sites)
    {
        foreach (var site in sites)
        {
            bool merged = false;
            foreach (var row in Sites)
            {
                if (row.Key != site.InstructionPointer) continue;
                row.Update(site);
                merged = true;
                break;
            }

            if (merged) continue;

            var added = new AccessSiteRow(site);
            Sites.Add(added);
            Selected ??= added;
            Raise(nameof(FoundNothing));
        }

        if (Selected is null) return;

        // The selected row keeps the most recent hit, so its detail is stale as soon as it fires again.
        Raise(nameof(RegisterText));
        Raise(nameof(CodeText));
        Raise(nameof(BaseExplanation));
        Raise(nameof(CanTraceRoute));
    }

    /// <summary>Detaches. Nothing else in the app is allowed to leave a debugger on the game.</summary>
    public void Stop()
    {
        _timer.Stop();
        if (_watch is not { } watch) return;

        watch.Stop();
        HitCount = watch.HitCount;
        Merge(watch.Snapshot());
        Status = watch.Status;

        Raise(nameof(IsWatching));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        Stop();
        _watch?.Dispose();
        _watch = null;
    }
}
