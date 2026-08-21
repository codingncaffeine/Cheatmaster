using System.Globalization;

namespace Cheatmaster.Core.Scanning;

public enum ScanType : byte
{
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double
}

public static class ScanTypes
{
    public static readonly ScanType[] All =
    [
        ScanType.Int8, ScanType.UInt8, ScanType.Int16, ScanType.UInt16,
        ScanType.Int32, ScanType.UInt32, ScanType.Int64, ScanType.UInt64,
        ScanType.Float, ScanType.Double
    ];

    public static int Width(this ScanType t) => t switch
    {
        ScanType.Int8 or ScanType.UInt8 => 1,
        ScanType.Int16 or ScanType.UInt16 => 2,
        ScanType.Int32 or ScanType.UInt32 or ScanType.Float => 4,
        _ => 8
    };

    public static bool IsFloat(this ScanType t) => t is ScanType.Float or ScanType.Double;

    public static bool IsSigned(this ScanType t) =>
        t is ScanType.Int8 or ScanType.Int16 or ScanType.Int32 or ScanType.Int64 or ScanType.Float or ScanType.Double;

    public static string Label(this ScanType t) => t switch
    {
        ScanType.Int8 => "Int8",
        ScanType.UInt8 => "UInt8",
        ScanType.Int16 => "Int16",
        ScanType.UInt16 => "UInt16",
        ScanType.Int32 => "Int32",
        ScanType.UInt32 => "UInt32",
        ScanType.Int64 => "Int64",
        ScanType.UInt64 => "UInt64",
        ScanType.Float => "Float",
        _ => "Double"
    };

    /// <summary>The wording most guides use, kept alongside the precise name for searchability.</summary>
    public static string FriendlyLabel(this ScanType t) => t switch
    {
        ScanType.Int8 => "1 Byte",
        ScanType.UInt8 => "1 Byte (unsigned)",
        ScanType.Int16 => "2 Bytes",
        ScanType.UInt16 => "2 Bytes (unsigned)",
        ScanType.Int32 => "4 Bytes",
        ScanType.UInt32 => "4 Bytes (unsigned)",
        ScanType.Int64 => "8 Bytes",
        ScanType.UInt64 => "8 Bytes (unsigned)",
        ScanType.Float => "Float",
        _ => "Double"
    };

    public static double MinValue(this ScanType t) => t switch
    {
        ScanType.Int8 => sbyte.MinValue,
        ScanType.UInt8 => byte.MinValue,
        ScanType.Int16 => short.MinValue,
        ScanType.UInt16 => ushort.MinValue,
        ScanType.Int32 => int.MinValue,
        ScanType.UInt32 => uint.MinValue,
        ScanType.Int64 => long.MinValue,
        ScanType.UInt64 => ulong.MinValue,
        ScanType.Float => -float.MaxValue,
        _ => -double.MaxValue
    };

    public static double MaxValue(this ScanType t) => t switch
    {
        ScanType.Int8 => sbyte.MaxValue,
        ScanType.UInt8 => byte.MaxValue,
        ScanType.Int16 => short.MaxValue,
        ScanType.UInt16 => ushort.MaxValue,
        ScanType.Int32 => int.MaxValue,
        ScanType.UInt32 => uint.MaxValue,
        ScanType.Int64 => long.MaxValue,
        ScanType.UInt64 => ulong.MaxValue,
        ScanType.Float => float.MaxValue,
        _ => double.MaxValue
    };
}

/// <summary>
/// Conversions between a raw little-endian bit pattern (held in the low bytes of a ulong)
/// and the number it represents. Every result the scanner stores is one of these patterns,
/// which keeps 64-bit integers exact instead of routing them through double.
/// </summary>
public static class Raw
{
    public static ulong Mask(ScanType t) => t.Width() switch
    {
        1 => 0xFFUL,
        2 => 0xFFFFUL,
        4 => 0xFFFF_FFFFUL,
        _ => ulong.MaxValue
    };

    public static double ToDouble(ScanType t, ulong bits) => t switch
    {
        ScanType.Int8 => (sbyte)(byte)bits,
        ScanType.UInt8 => (byte)bits,
        ScanType.Int16 => (short)(ushort)bits,
        ScanType.UInt16 => (ushort)bits,
        ScanType.Int32 => (int)(uint)bits,
        ScanType.UInt32 => (uint)bits,
        ScanType.Int64 => (long)bits,
        ScanType.UInt64 => bits,
        ScanType.Float => BitConverter.UInt32BitsToSingle((uint)bits),
        _ => BitConverter.UInt64BitsToDouble(bits)
    };

    public static int Compare(ScanType t, ulong a, ulong b) => t switch
    {
        ScanType.Int8 => ((sbyte)(byte)a).CompareTo((sbyte)(byte)b),
        ScanType.UInt8 => ((byte)a).CompareTo((byte)b),
        ScanType.Int16 => ((short)(ushort)a).CompareTo((short)(ushort)b),
        ScanType.UInt16 => ((ushort)a).CompareTo((ushort)b),
        ScanType.Int32 => ((int)(uint)a).CompareTo((int)(uint)b),
        ScanType.UInt32 => ((uint)a).CompareTo((uint)b),
        ScanType.Int64 => ((long)a).CompareTo((long)b),
        ScanType.UInt64 => a.CompareTo(b),
        ScanType.Float => BitConverter.UInt32BitsToSingle((uint)a).CompareTo(BitConverter.UInt32BitsToSingle((uint)b)),
        _ => BitConverter.UInt64BitsToDouble(a).CompareTo(BitConverter.UInt64BitsToDouble(b))
    };

    public static string Format(ScanType t, ulong bits)
    {
        switch (t)
        {
            case ScanType.Float:
                float f = BitConverter.UInt32BitsToSingle((uint)bits);
                return f.ToString("0.######", CultureInfo.InvariantCulture);
            case ScanType.Double:
                double d = BitConverter.UInt64BitsToDouble(bits);
                return d.ToString("0.############", CultureInfo.InvariantCulture);
            case ScanType.Int8: return ((sbyte)(byte)bits).ToString(CultureInfo.InvariantCulture);
            case ScanType.UInt8: return ((byte)bits).ToString(CultureInfo.InvariantCulture);
            case ScanType.Int16: return ((short)(ushort)bits).ToString(CultureInfo.InvariantCulture);
            case ScanType.UInt16: return ((ushort)bits).ToString(CultureInfo.InvariantCulture);
            case ScanType.Int32: return ((int)(uint)bits).ToString(CultureInfo.InvariantCulture);
            case ScanType.UInt32: return ((uint)bits).ToString(CultureInfo.InvariantCulture);
            case ScanType.Int64: return ((long)bits).ToString(CultureInfo.InvariantCulture);
            default: return bits.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static string FormatHex(ScanType t, ulong bits) =>
        "0x" + (bits & Mask(t)).ToString("X" + (t.Width() * 2), CultureInfo.InvariantCulture);

    /// <summary>Reverses the byte order of the significant bytes, which is how big-endian storage is expressed.</summary>
    public static ulong SwapBytes(ScanType t, ulong bits)
    {
        int w = t.Width();
        ulong result = 0;
        for (int i = 0; i < w; i++)
        {
            result = (result << 8) | (bits & 0xFF);
            bits >>= 8;
        }
        return result;
    }

    public static bool TryFromDecimal(ScanType t, decimal value, out ulong bits)
    {
        bits = 0;
        switch (t)
        {
            case ScanType.Int8:
                if (value < sbyte.MinValue || value > sbyte.MaxValue || decimal.Truncate(value) != value) return false;
                bits = (byte)(sbyte)value; return true;
            case ScanType.UInt8:
                if (value < byte.MinValue || value > byte.MaxValue || decimal.Truncate(value) != value) return false;
                bits = (byte)value; return true;
            case ScanType.Int16:
                if (value < short.MinValue || value > short.MaxValue || decimal.Truncate(value) != value) return false;
                bits = (ushort)(short)value; return true;
            case ScanType.UInt16:
                if (value < ushort.MinValue || value > ushort.MaxValue || decimal.Truncate(value) != value) return false;
                bits = (ushort)value; return true;
            case ScanType.Int32:
                if (value < int.MinValue || value > int.MaxValue || decimal.Truncate(value) != value) return false;
                bits = (uint)(int)value; return true;
            case ScanType.UInt32:
                if (value < uint.MinValue || value > uint.MaxValue || decimal.Truncate(value) != value) return false;
                bits = (uint)value; return true;
            case ScanType.Int64:
                if (value < long.MinValue || value > long.MaxValue || decimal.Truncate(value) != value) return false;
                bits = (ulong)(long)value; return true;
            case ScanType.UInt64:
                if (value < ulong.MinValue || value > ulong.MaxValue || decimal.Truncate(value) != value) return false;
                bits = (ulong)value; return true;
            case ScanType.Float:
                bits = BitConverter.SingleToUInt32Bits((float)value); return true;
            default:
                bits = BitConverter.DoubleToUInt64Bits((double)value); return true;
        }
    }

    public static bool TryFromDouble(ScanType t, double value, out ulong bits)
    {
        bits = 0;
        if (double.IsNaN(value)) return false;

        switch (t)
        {
            case ScanType.Float:
                if (Math.Abs(value) > float.MaxValue && !double.IsInfinity(value)) return false;
                bits = BitConverter.SingleToUInt32Bits((float)value);
                return true;
            case ScanType.Double:
                bits = BitConverter.DoubleToUInt64Bits(value);
                return true;
        }

        if (double.IsInfinity(value)) return false;
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < t.MinValue() || rounded > t.MaxValue()) return false;

        return t switch
        {
            ScanType.Int8 => Set((byte)(sbyte)rounded, out bits),
            ScanType.UInt8 => Set((byte)rounded, out bits),
            ScanType.Int16 => Set((ushort)(short)rounded, out bits),
            ScanType.UInt16 => Set((ushort)rounded, out bits),
            ScanType.Int32 => Set((uint)(int)rounded, out bits),
            ScanType.UInt32 => Set((uint)rounded, out bits),
            ScanType.Int64 => Set((ulong)(long)rounded, out bits),
            _ => Set((ulong)rounded, out bits)
        };

        static bool Set(ulong v, out ulong bits) { bits = v; return true; }
    }

    public static ulong MinBits(ScanType t) => t switch
    {
        ScanType.Int8 => unchecked((byte)sbyte.MinValue),
        ScanType.UInt8 => 0,
        ScanType.Int16 => unchecked((ushort)short.MinValue),
        ScanType.UInt16 => 0,
        ScanType.Int32 => unchecked((uint)int.MinValue),
        ScanType.UInt32 => 0,
        ScanType.Int64 => unchecked((ulong)long.MinValue),
        ScanType.UInt64 => 0,
        ScanType.Float => BitConverter.SingleToUInt32Bits(float.NegativeInfinity),
        _ => BitConverter.DoubleToUInt64Bits(double.NegativeInfinity)
    };

    public static ulong MaxBits(ScanType t) => t switch
    {
        ScanType.Int8 => (byte)sbyte.MaxValue,
        ScanType.UInt8 => byte.MaxValue,
        ScanType.Int16 => (ushort)short.MaxValue,
        ScanType.UInt16 => ushort.MaxValue,
        ScanType.Int32 => (uint)int.MaxValue,
        ScanType.UInt32 => uint.MaxValue,
        ScanType.Int64 => (ulong)long.MaxValue,
        ScanType.UInt64 => ulong.MaxValue,
        ScanType.Float => BitConverter.SingleToUInt32Bits(float.PositiveInfinity),
        _ => BitConverter.DoubleToUInt64Bits(double.PositiveInfinity)
    };

    /// <summary>The next representable value up, used to turn "greater than" into a closed range.</summary>
    public static ulong NextUp(ScanType t, ulong bits)
    {
        switch (t)
        {
            case ScanType.Float:
                return BitConverter.SingleToUInt32Bits(MathF.BitIncrement(BitConverter.UInt32BitsToSingle((uint)bits)));
            case ScanType.Double:
                return BitConverter.DoubleToUInt64Bits(Math.BitIncrement(BitConverter.UInt64BitsToDouble(bits)));
        }
        if (bits == MaxBits(t)) return bits;
        return (bits + 1) & Mask(t);
    }

    public static ulong NextDown(ScanType t, ulong bits)
    {
        switch (t)
        {
            case ScanType.Float:
                return BitConverter.SingleToUInt32Bits(MathF.BitDecrement(BitConverter.UInt32BitsToSingle((uint)bits)));
            case ScanType.Double:
                return BitConverter.DoubleToUInt64Bits(Math.BitDecrement(BitConverter.UInt64BitsToDouble(bits)));
        }
        if (bits == MinBits(t)) return bits;
        return (bits - 1) & Mask(t);
    }

    /// <summary>Writes the significant bytes of a raw pattern into a buffer, little-endian.</summary>
    public static void WriteBytes(ScanType t, ulong bits, Span<byte> destination)
    {
        int w = t.Width();
        for (int i = 0; i < w; i++)
            destination[i] = (byte)(bits >> (i * 8));
    }

    public static ulong ReadBits(ScanType t, ReadOnlySpan<byte> source)
    {
        int w = t.Width();
        ulong bits = 0;
        for (int i = 0; i < w; i++)
            bits |= (ulong)source[i] << (i * 8);
        return bits;
    }
}
