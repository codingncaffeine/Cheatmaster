using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.Core.Cheats;

/// <summary>
/// Keeps frozen values pinned by rewriting them on a timer. The encoded pattern is worked out
/// when the list changes rather than on every tick, so the loop stays a resolve-and-write.
/// </summary>
public sealed class ValueFreezer : IDisposable
{
    private readonly record struct Pinned(CheatEntry Entry, ScanType Type, ulong Bits, int Width);

    private readonly Lock _gate = new();
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _loop;

    private Pinned[] _pinned = [];
    private TargetProcess? _process;
    private long _writes;

    public ValueFreezer(TimeSpan? interval = null)
    {
        _interval = interval ?? TimeSpan.FromMilliseconds(40);

        // A thread of its own rather than a pool timer: a scan fills the thread pool with
        // parallel work, and a freeze that waits behind it is a freeze that lets go.
        _loop = new Thread(Run) { IsBackground = true, Name = "Cheatmaster freezer" };
        _loop.Start();
    }

    /// <summary>Total successful writes since start, so the UI can show that freezing is live.</summary>
    public long Writes => Interlocked.Read(ref _writes);

    public int PinnedCount
    {
        get
        {
            lock (_gate) return _pinned.Length;
        }
    }

    public void Attach(TargetProcess? process)
    {
        lock (_gate) _process = process;
    }

    /// <summary>Replaces the frozen set. Entries whose value cannot be encoded are dropped here, not in the loop.</summary>
    public void Update(IEnumerable<CheatEntry> entries)
    {
        var list = new List<Pinned>();
        foreach (var entry in entries)
        {
            if (!entry.Frozen) continue;

            var value = UserValue.Parse(entry.FreezeValue);
            if (!value.IsValid) continue;

            double display = value.FitsDecimal ? (double)value.Dec : value.Dbl;
            if (!entry.Interpretation.TryEncodeExact(display, out ulong bits)) continue;

            list.Add(new Pinned(entry, entry.Type, bits, entry.Type.Width()));
        }

        lock (_gate) _pinned = [.. list];
    }

    private void Run()
    {
        var token = _cancellation.Token;
        while (!token.WaitHandle.WaitOne(_interval))
        {
            try
            {
                Tick();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One bad write must not end freezing for every other entry.
            }
        }
    }

    private void Tick()
    {
        Pinned[] pinned;
        TargetProcess? process;
        lock (_gate)
        {
            pinned = _pinned;
            process = _process;
        }

        if (process is null || pinned.Length == 0 || !process.IsOpen) return;

        Span<byte> buffer = stackalloc byte[8];
        long written = 0;
        foreach (var pin in pinned)
        {
            ulong address = pin.Entry.Address.Resolve(process);
            if (address == 0) continue;

            // Uses the type captured with the value: reading it live could disagree with the
            // width below and leave stale bytes from the previous entry in the buffer.
            Raw.WriteBytes(pin.Type, pin.Bits, buffer);
            if (process.Write(address, buffer[..pin.Width])) written++;
        }

        if (written > 0) Interlocked.Add(ref _writes, written);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _loop.Join(TimeSpan.FromSeconds(1));
    }
}
