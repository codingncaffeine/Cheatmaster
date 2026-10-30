using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Cheatmaster.App.Infrastructure;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.ViewModels;

public sealed class ResultRow : ObservableObject
{
    private ulong _current;
    private bool _changed;

    public ResultRow(int index, ulong address, Interpretation interpretation, ulong value)
    {
        Index = index;
        Address = address;
        Interpretation = interpretation;
        _current = value;
        FoundValue = value;
    }

    public int Index { get; }
    public ulong Address { get; }
    public Interpretation Interpretation { get; }
    public ulong FoundValue { get; }

    /// <summary>The value as it stands right now, which is what a route has to read back.</summary>
    public ulong CurrentValue => _current;

    public string AddressText => Address.ToString("X", CultureInfo.InvariantCulture);
    public string TypeLabel => Interpretation.Label;
    public string ValueText => Interpretation.FormatDisplay(_current);
    public string FoundText => Interpretation.FormatDisplay(FoundValue);

    /// <summary>Highlights rows whose value moved since the scan, which is usually the one you want.</summary>
    public bool Changed
    {
        get => _changed;
        private set => Set(ref _changed, value);
    }

    public void Refresh(TargetProcess process)
    {
        Span<byte> buffer = stackalloc byte[8];
        int width = Interpretation.Width;
        if (!process.ReadExact(Address, buffer[..width])) return;

        ulong bits = Raw.ReadBits(Interpretation.Type, buffer);
        if (bits == _current) return;

        _current = bits;
        Changed = bits != FoundValue;
        Raise(nameof(ValueText));
    }
}

/// <summary>
/// Presents a scan result set to the grid without building a view model per hit. A first scan
/// can return millions of addresses; only the handful on screen ever become objects.
/// </summary>
public sealed class LazyResultList : IList<ResultRow>, IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    public const int DisplayCap = 1_000_000;

    private readonly ScanResults _results;
    private readonly int[]? _map;
    private readonly Dictionary<int, ResultRow> _cache = [];

    public LazyResultList(ScanResults results)
    {
        _results = results;
        _map = ResultView.Build(results, out int hidden);
        HiddenCount = hidden;
        int shown = _map?.Length ?? results.Count;
        Count = Math.Min(shown, DisplayCap);
        TotalCount = shown;
    }

    /// <summary>Hits left out because they only fit encodings too common to tell anything apart.</summary>
    public int HiddenCount { get; }

    /// <summary>True when each row is one address rather than one address per encoding.</summary>
    public bool IsByAddress => _map is not null;

    public int Count { get; }
    public int TotalCount { get; }

    /// <summary>How many rows have been built so far: the grid should only ever ask for the ones it shows.</summary>
    internal int RealizedCount => _cache.Count;
    public bool IsCapped => TotalCount > Count;

    public event NotifyCollectionChangedEventHandler? CollectionChanged { add { } remove { } }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public ResultRow this[int index]
    {
        get
        {
            if (_cache.TryGetValue(index, out var row)) return row;
            int at = _map is null ? index : _map[index];
            row = new ResultRow(index, _results.Addresses[at], _results.InterpretationAt(at), _results.Values[at]);
            _cache[index] = row;
            return row;
        }
        set => throw new NotSupportedException();
    }

    /// <summary>Rows already on screen, so the refresh timer only re-reads what is visible.</summary>
    public IEnumerable<ResultRow> Realized(int first, int last)
    {
        first = Math.Max(0, first);
        last = Math.Min(Count - 1, last);
        for (int i = first; i <= last; i++)
        {
            if (_cache.TryGetValue(i, out var row)) yield return row;
        }
    }

    public IEnumerator<ResultRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(ResultRow item) => item is null ? -1 : item.Index < Count ? item.Index : -1;

    public bool Contains(ResultRow item) => IndexOf(item) >= 0;

    public void CopyTo(ResultRow[] array, int arrayIndex)
    {
        for (int i = 0; i < Count; i++) array[arrayIndex + i] = this[i];
    }

    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public void Insert(int index, ResultRow item) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void Add(ResultRow item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Remove(ResultRow item) => throw new NotSupportedException();
    int IList.Add(object? value) => throw new NotSupportedException();
    bool IList.Contains(object? value) => value is ResultRow row && Contains(row);
    int IList.IndexOf(object? value) => value is ResultRow row ? IndexOf(row) : -1;
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i);
    }
}

/// <summary>
/// Decides what the results list shows, so a player sees candidates rather than every way a
/// number could be stored.
///
/// Encodings that hit their cap are noise once anything else survives: a small number fits
/// millions of places as a byte or a scaled short, and listing them buries the handful of real
/// candidates. They stay in the session (the next scan still checks them) but are not shown.
/// And one address that fits several encodings is one candidate, so it is one row, under the
/// encoding games most often use for a counter.
/// </summary>
internal static class ResultView
{
    /// <summary>Beyond this many shown hits, listing every one is all that is affordable.</summary>
    private const int DedupeLimit = 2_000_000;

    public static int[]? Build(ScanResults results, out int hidden)
    {
        hidden = 0;
        var capped = new HashSet<int>();
        bool anyUsable = false;
        foreach (var group in results.Groups)
        {
            if (group.Capped) capped.Add(group.InterpId);
            else if (group.Count > 0) anyUsable = true;
        }

        // Nothing but noise survived: show it all, since it is all there is.
        if (!anyUsable) return null;

        int kept = 0;
        for (int i = 0; i < results.Count; i++)
            if (!capped.Contains(results.InterpIds[i])) kept++;
        hidden = results.Count - kept;

        if (kept > DedupeLimit)
        {
            var plain = new int[kept];
            int n = 0;
            for (int i = 0; i < results.Count; i++)
                if (!capped.Contains(results.InterpIds[i])) plain[n++] = i;
            return plain;
        }

        var best = new Dictionary<ulong, int>(kept);
        var order = new List<ulong>(kept);
        for (int i = 0; i < results.Count; i++)
        {
            if (capped.Contains(results.InterpIds[i])) continue;
            ulong address = results.Addresses[i];
            if (best.TryGetValue(address, out int current))
            {
                if (Rank(results.InterpretationAt(i)) < Rank(results.InterpretationAt(current))) best[address] = i;
            }
            else
            {
                best[address] = i;
                order.Add(address);
            }
        }

        // Most likely first, then by address so the list reads steadily.
        order.Sort((a, b) =>
        {
            int ra = Rank(results.InterpretationAt(best[a])), rb = Rank(results.InterpretationAt(best[b]));
            return ra != rb ? ra.CompareTo(rb) : a.CompareTo(b);
        });

        var map = new int[order.Count];
        for (int i = 0; i < order.Count; i++) map[i] = best[order[i]];
        return map;
    }

    /// <summary>
    /// How likely an encoding is for a value a player can see, lowest first. Plain 32-bit whole
    /// numbers and floats are what game counters almost always are; picking the 4-byte reading
    /// also keeps a later write from spilling into the field next door.
    /// </summary>
    public static int Rank(in Interpretation interp)
    {
        int type = interp.Type switch
        {
            ScanType.Int32 => 0,
            ScanType.Float => 1,
            ScanType.UInt32 => 2,
            ScanType.Int64 => 3,
            ScanType.Double => 4,
            ScanType.UInt64 => 5,
            ScanType.Int16 => 6,
            ScanType.UInt16 => 7,
            _ => 8
        };
        return interp.IsPlain ? type : 20 + type;
    }
}
