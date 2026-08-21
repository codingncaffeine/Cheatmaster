using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cheatmaster.Core.Scanning;

internal enum DiffOp : byte
{
    Changed,
    Unchanged,
    Increased,
    Decreased,

    /// <summary>before - after equals the operand. A negative operand means it went up.</summary>
    DeltaEquals,

    /// <summary>before XOR after equals the operand, which is how a constant XOR key gives itself away.</summary>
    XorEquals
}

/// <summary>One address whose two readings are related the way the search asked for.</summary>
internal readonly record struct DiffHit(ulong Address, ScanType Type, DiffOp Relation, ulong Before, ulong After);

internal sealed class DiffSink
{
    public List<DiffHit> Hits { get; } = [];
    public int Limit { get; init; } = int.MaxValue;
    public bool Full => Hits.Count >= Limit;

    public void Add(in DiffHit hit)
    {
        if (!Full) Hits.Add(hit);
    }
}

/// <summary>
/// Compares two readings of the same address taken at different moments.
///
/// This is what finds values an exact search cannot. A game that stores health as
/// <c>value XOR key</c> with a key chosen at random hides the number completely, but the key
/// cancels out across two readings: <c>before XOR after</c> equals <c>encode(A) XOR encode(B)</c>
/// no matter what the key was. Matching on that relation finds the address, and the key falls
/// out as <c>before XOR encode(A)</c> — which is what makes the value writable afterwards.
/// </summary>
internal static class DiffKernel
{
    public static void Compare(ScanType type, DiffOp op, ulong operandBits,
        ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, ulong baseAddress,
        int coreLength, int alignment, DiffSink sink)
    {
        if (op == DiffOp.XorEquals)
        {
            // XOR is a bit operation, so the unsigned type of the same width covers every case,
            // floats included.
            switch (type.Width())
            {
                case 1: CompareXor<byte>(type, before, after, baseAddress, coreLength, alignment, operandBits, sink); return;
                case 2: CompareXor<ushort>(type, before, after, baseAddress, coreLength, alignment, operandBits, sink); return;
                case 4: CompareXor<uint>(type, before, after, baseAddress, coreLength, alignment, operandBits, sink); return;
                default: CompareXor<ulong>(type, before, after, baseAddress, coreLength, alignment, operandBits, sink); return;
            }
        }

        switch (type)
        {
            case ScanType.Int8: Compare<sbyte>(type, op, operandBits, before, after, baseAddress, coreLength, alignment, sink); break;
            case ScanType.UInt8: Compare<byte>(type, op, operandBits, before, after, baseAddress, coreLength, alignment, sink); break;
            case ScanType.Int16: Compare<short>(type, op, operandBits, before, after, baseAddress, coreLength, alignment, sink); break;
            case ScanType.UInt16: Compare<ushort>(type, op, operandBits, before, after, baseAddress, coreLength, alignment, sink); break;
            case ScanType.Int32: Compare<int>(type, op, operandBits, before, after, baseAddress, coreLength, alignment, sink); break;
            case ScanType.UInt32: Compare<uint>(type, op, operandBits, before, after, baseAddress, coreLength, alignment, sink); break;
            case ScanType.Int64: Compare<long>(type, op, operandBits, before, after, baseAddress, coreLength, alignment, sink); break;
            case ScanType.UInt64: Compare<ulong>(type, op, operandBits, before, after, baseAddress, coreLength, alignment, sink); break;
            case ScanType.Float: Compare<float>(type, op, operandBits, before, after, baseAddress, coreLength, alignment, sink); break;
            default: Compare<double>(type, op, operandBits, before, after, baseAddress, coreLength, alignment, sink); break;
        }
    }

    private static void Compare<T>(ScanType type, DiffOp op, ulong operandBits,
        ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, ulong baseAddress,
        int coreLength, int alignment, DiffSink sink) where T : unmanaged, INumber<T>
    {
        int width = Unsafe.SizeOf<T>();
        int usable = Math.Min(before.Length, after.Length);
        if (usable < width || coreLength <= 0) return;

        T operand = Unsafe.As<ulong, T>(ref operandBits);
        int step = alignment <= 0 ? 1 : alignment;
        if (step > width) step = width;
        if (!BitOperations.IsPow2(step)) step = 1;

        for (int start = 0; start < width; start += step)
        {
            if (start >= usable) break;
            var oldTyped = MemoryMarshal.Cast<byte, T>(before[start..usable]);
            var newTyped = MemoryMarshal.Cast<byte, T>(after[start..usable]);
            int n = Math.Min(oldTyped.Length, newTyped.Length);
            if (n == 0) continue;

            int available = coreLength - start;
            if (available <= 0) continue;
            int maxCount = (available + width - 1) / width;
            if (maxCount < n) n = maxCount;

            int i = 0;
            int lanes = Vector<T>.Count;
            if (Vector.IsHardwareAccelerated && n >= lanes)
            {
                var operandV = new Vector<T>(operand);
                var zero = Vector<T>.Zero;
                int limit = n - lanes;

                for (; i <= limit; i += lanes)
                {
                    var a = new Vector<T>(oldTyped.Slice(i, lanes));
                    var b = new Vector<T>(newTyped.Slice(i, lanes));

                    Vector<T> mask = op switch
                    {
                        DiffOp.Changed => Vector.OnesComplement(Vector.Equals<T>(a, b)),
                        DiffOp.Unchanged => Vector.Equals<T>(a, b),
                        DiffOp.Increased => Vector.GreaterThan<T>(b, a),
                        DiffOp.Decreased => Vector.LessThan<T>(b, a),
                        _ => Vector.Equals<T>(Vector.Subtract(a, b), operandV)
                    };

                    if (Vector.EqualsAll<T>(mask, zero)) continue;

                    for (int k = 0; k < lanes; k++)
                    {
                        if (Matches(op, oldTyped[i + k], newTyped[i + k], operand))
                            Emit(sink, type, op, baseAddress, start, i + k, width, oldTyped[i + k], newTyped[i + k]);
                    }

                    if (sink.Full) return;
                }
            }

            for (; i < n; i++)
            {
                if (Matches(op, oldTyped[i], newTyped[i], operand))
                    Emit(sink, type, op, baseAddress, start, i, width, oldTyped[i], newTyped[i]);
                if (sink.Full) return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Matches<T>(DiffOp op, T before, T after, T operand) where T : unmanaged, INumber<T> => op switch
    {
        DiffOp.Changed => before != after,
        DiffOp.Unchanged => before == after,
        DiffOp.Increased => after > before,
        DiffOp.Decreased => after < before,
        _ => before - after == operand
    };

    private static void CompareXor<T>(ScanType type, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after,
        ulong baseAddress, int coreLength, int alignment, ulong operandBits, DiffSink sink)
        where T : unmanaged, IBitwiseOperators<T, T, T>, IEqualityOperators<T, T, bool>
    {
        int width = Unsafe.SizeOf<T>();
        int usable = Math.Min(before.Length, after.Length);
        if (usable < width || coreLength <= 0) return;

        T operand = Unsafe.As<ulong, T>(ref operandBits);
        int step = alignment <= 0 ? 1 : alignment;
        if (step > width) step = width;
        if (!BitOperations.IsPow2(step)) step = 1;

        for (int start = 0; start < width; start += step)
        {
            if (start >= usable) break;
            var oldTyped = MemoryMarshal.Cast<byte, T>(before[start..usable]);
            var newTyped = MemoryMarshal.Cast<byte, T>(after[start..usable]);
            int n = Math.Min(oldTyped.Length, newTyped.Length);
            if (n == 0) continue;

            int available = coreLength - start;
            if (available <= 0) continue;
            int maxCount = (available + width - 1) / width;
            if (maxCount < n) n = maxCount;

            int i = 0;
            int lanes = Vector<T>.Count;
            if (Vector.IsHardwareAccelerated && n >= lanes)
            {
                var operandV = new Vector<T>(operand);
                var zero = Vector<T>.Zero;
                int limit = n - lanes;

                for (; i <= limit; i += lanes)
                {
                    var a = new Vector<T>(oldTyped.Slice(i, lanes));
                    var b = new Vector<T>(newTyped.Slice(i, lanes));
                    var mask = Vector.Equals<T>(Vector.Xor(a, b), operandV);
                    if (Vector.EqualsAll<T>(mask, zero)) continue;

                    for (int k = 0; k < lanes; k++)
                    {
                        if ((oldTyped[i + k] ^ newTyped[i + k]) == operand)
                            Emit(sink, type, DiffOp.XorEquals, baseAddress, start, i + k, width, oldTyped[i + k], newTyped[i + k]);
                    }

                    if (sink.Full) return;
                }
            }

            for (; i < n; i++)
            {
                if ((oldTyped[i] ^ newTyped[i]) == operand)
                    Emit(sink, type, DiffOp.XorEquals, baseAddress, start, i, width, oldTyped[i], newTyped[i]);
                if (sink.Full) return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Emit<T>(DiffSink sink, ScanType type, DiffOp op, ulong baseAddress,
        int start, int index, int width, T before, T after) where T : unmanaged
    {
        ulong beforeBits = 0;
        ulong afterBits = 0;
        Unsafe.As<ulong, T>(ref beforeBits) = before;
        Unsafe.As<ulong, T>(ref afterBits) = after;
        sink.Add(new DiffHit(baseAddress + (ulong)(start + index * width), type, op, beforeBits, afterBits));
    }
}
