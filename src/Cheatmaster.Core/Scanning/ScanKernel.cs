using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cheatmaster.Core.Scanning;

/// <summary>
/// The inner comparison loop. Every storage theory reduces to "is the value at this offset,
/// read as T, inside [lo, hi]", so one vectorised generic routine covers all of them, and
/// byte-swapped or XOR-encoded theories collapse to a point range over the same routine.
/// </summary>
internal static class ScanKernel
{
    /// <summary>
    /// Scans one buffer for one plan item.
    /// <paramref name="coreLength"/> is how much of the buffer belongs to this chunk; bytes past
    /// it are overlap read so a value straddling the boundary is still found, and are reported
    /// by the next chunk instead.
    /// </summary>
    public static void Scan(in ScanPlanItem item, ReadOnlySpan<byte> data, ulong dataBase, int coreLength, int alignment, HitBuffer sink)
    {
        switch (item.Type)
        {
            case ScanType.Int8: ScanRange<sbyte>(data, dataBase, coreLength, alignment, item, sink); break;
            case ScanType.UInt8: ScanRange<byte>(data, dataBase, coreLength, alignment, item, sink); break;
            case ScanType.Int16: ScanRange<short>(data, dataBase, coreLength, alignment, item, sink); break;
            case ScanType.UInt16: ScanRange<ushort>(data, dataBase, coreLength, alignment, item, sink); break;
            case ScanType.Int32: ScanRange<int>(data, dataBase, coreLength, alignment, item, sink); break;
            case ScanType.UInt32: ScanRange<uint>(data, dataBase, coreLength, alignment, item, sink); break;
            case ScanType.Int64: ScanRange<long>(data, dataBase, coreLength, alignment, item, sink); break;
            case ScanType.UInt64: ScanRange<ulong>(data, dataBase, coreLength, alignment, item, sink); break;
            case ScanType.Float: ScanRange<float>(data, dataBase, coreLength, alignment, item, sink); break;
            default: ScanRange<double>(data, dataBase, coreLength, alignment, item, sink); break;
        }
    }

    private static void ScanRange<T>(ReadOnlySpan<byte> data, ulong dataBase, int coreLength, int alignment,
        in ScanPlanItem item, HitBuffer sink) where T : unmanaged, INumber<T>
    {
        int width = Unsafe.SizeOf<T>();
        if (data.Length < width || coreLength <= 0) return;

        ulong loBits = item.LoBits;
        ulong hiBits = item.HiBits;
        T lo = Unsafe.As<ulong, T>(ref loBits);
        T hi = Unsafe.As<ulong, T>(ref hiBits);
        bool point = loBits == hiBits;
        int interpId = item.InterpId;

        // Candidate addresses are multiples of the alignment. Scanning by element stride covers
        // one residue class per pass, so an alignment finer than the type needs several passes.
        int step = alignment <= 0 ? 1 : alignment;
        if (step > width) step = width;
        if (!BitOperations.IsPow2(step)) step = 1;

        for (int start = 0; start < width; start += step)
        {
            if (start >= data.Length) break;
            ReadOnlySpan<T> typed = MemoryMarshal.Cast<byte, T>(data[start..]);
            int n = typed.Length;
            if (n == 0) continue;

            int available = coreLength - start;
            if (available <= 0) continue;
            int maxCount = (available + width - 1) / width;
            if (maxCount < n) n = maxCount;

            int i = 0;
            int vcount = Vector<T>.Count;
            if (Vector.IsHardwareAccelerated && n >= vcount)
            {
                var loV = new Vector<T>(lo);
                var hiV = new Vector<T>(hi);
                var zero = Vector<T>.Zero;
                int limit = n - vcount;

                for (; i <= limit; i += vcount)
                {
                    var chunk = new Vector<T>(typed.Slice(i, vcount));
                    Vector<T> mask = point
                        ? Vector.Equals<T>(chunk, loV)
                        : Vector.BitwiseAnd(Vector.GreaterThanOrEqual<T>(chunk, loV), Vector.LessThanOrEqual<T>(chunk, hiV));

                    // A set lane is all-ones, which is NaN for the float types, so a plain
                    // equality test against zero detects hits for every T.
                    if (Vector.EqualsAll<T>(mask, zero)) continue;

                    for (int k = 0; k < vcount; k++)
                    {
                        T v = typed[i + k];
                        if (point ? v == lo : v >= lo && v <= hi)
                            Emit(sink, dataBase, start, i + k, width, interpId, v);
                    }
                }
            }

            for (; i < n; i++)
            {
                T v = typed[i];
                if (point ? v == lo : v >= lo && v <= hi)
                    Emit(sink, dataBase, start, i, width, interpId, v);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Emit<T>(HitBuffer sink, ulong dataBase, int start, int index, int width, int interpId, T value)
        where T : unmanaged
    {
        ulong bits = 0;
        Unsafe.As<ulong, T>(ref bits) = value;
        sink.Add(dataBase + (ulong)(start + index * width), interpId, bits);
    }

    /// <summary>Reads the stored pattern for one already-known address out of a buffer.</summary>
    public static ulong ReadStored(ScanType type, ReadOnlySpan<byte> source) => Raw.ReadBits(type, source);
}
