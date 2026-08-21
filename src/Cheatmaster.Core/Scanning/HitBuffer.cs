namespace Cheatmaster.Core.Scanning;

/// <summary>
/// Growable parallel arrays of scan hits. Three arrays instead of an array of structs keeps
/// the per-hit cost at 20 bytes, which matters when a first scan finds tens of millions.
/// </summary>
public sealed class HitBuffer
{
    private ulong[] _addresses;
    private int[] _interpretations;
    private ulong[] _values;

    public HitBuffer(int capacity = 1024)
    {
        capacity = Math.Max(capacity, 16);
        _addresses = new ulong[capacity];
        _interpretations = new int[capacity];
        _values = new ulong[capacity];
    }

    public int Count { get; private set; }

    public ulong[] Addresses => _addresses;
    public int[] Interpretations => _interpretations;
    public ulong[] Values => _values;

    public void Add(ulong address, int interpretation, ulong value)
    {
        if (Count == _addresses.Length) Grow();
        _addresses[Count] = address;
        _interpretations[Count] = interpretation;
        _values[Count] = value;
        Count++;
    }

    private void Grow()
    {
        int next = _addresses.Length * 2;
        Array.Resize(ref _addresses, next);
        Array.Resize(ref _interpretations, next);
        Array.Resize(ref _values, next);
    }

    public void Clear() => Count = 0;

    /// <summary>Sorts by address so merged buffers stay in address order.</summary>
    public void Sort()
    {
        if (Count < 2) return;

        var keys = new ulong[Count];
        Array.Copy(_addresses, keys, Count);
        var order = new int[Count];
        for (int i = 0; i < Count; i++) order[i] = i;
        Array.Sort(keys, order);

        var na = new ulong[Count];
        var ni = new int[Count];
        var nv = new ulong[Count];
        for (int i = 0; i < Count; i++)
        {
            int src = order[i];
            na[i] = _addresses[src];
            ni[i] = _interpretations[src];
            nv[i] = _values[src];
        }

        _addresses = na;
        _interpretations = ni;
        _values = nv;
    }
}
