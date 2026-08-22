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
    private readonly Dictionary<int, ResultRow> _cache = [];

    public LazyResultList(ScanResults results)
    {
        _results = results;
        Count = Math.Min(results.Count, DisplayCap);
        TotalCount = results.Count;
    }

    public int Count { get; }
    public int TotalCount { get; }
    public bool IsCapped => TotalCount > Count;

    public event NotifyCollectionChangedEventHandler? CollectionChanged { add { } remove { } }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public ResultRow this[int index]
    {
        get
        {
            if (_cache.TryGetValue(index, out var row)) return row;
            row = new ResultRow(index, _results.Addresses[index], _results.InterpretationAt(index), _results.Values[index]);
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
