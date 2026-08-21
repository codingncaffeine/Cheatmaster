using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.Core.Cheats;

/// <summary>
/// Keeps frozen values pinned by rewriting them on a timer. The encoded pattern is worked out
/// when the list changes rather than on every tick, so the loop stays a resolve-and-write.
/// </summary>
public sealed class ValueFreezer : IDisposable
{
    private readonly record struct Pinned(CheatEntry Entry, ulong Bits, int Width);

    private readonly Lock _gate = new();
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _loop;

    private Pinned[] _pinned = [];
    private TargetProcess? _process;
    private long _writes;

    public ValueFreezer(TimeSpan? interval = null)
    {
        _timer = new PeriodicTimer(interval ?? TimeSpan.FromMilliseconds(40));
        _loop = Task.Run(RunAsync);
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

            list.Add(new Pinned(entry, bits, entry.Type.Width()));
        }

        lock (_gate) _pinned = [.. list];
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cancellation.Token).ConfigureAwait(false))
                Tick();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
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

            Raw.WriteBytes(pin.Entry.Type, pin.Bits, buffer);
            if (process.Write(address, buffer[..pin.Width])) written++;
        }

        if (written > 0) Interlocked.Add(ref _writes, written);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _timer.Dispose();
        try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { /* shutting down */ }
        _cancellation.Dispose();
    }
}
