using System.Globalization;

namespace Cheatmaster.Core.Scanning;

/// <summary>
/// One theory about how a number the player sees is stored in memory: its machine type, its
/// byte order, a fixed scale factor, an additive bias, and an optional XOR key.
///
/// The scanner does not ask the user to pick one. It tests many at once and reports which
/// ones survived, which is what turns "wrong type, no results" into a solved search.
///
/// stored = (display * ScaleNum / ScaleDen) + Bias, encoded as Type, byte-swapped when
/// BigEndian, then XORed with XorKey.
/// </summary>
public readonly record struct Interpretation(
    ScanType Type,
    int ScaleNum,
    int ScaleDen,
    bool BigEndian,
    ulong XorKey,
    long Bias)
{
    public Interpretation(ScanType type) : this(type, 1, 1, false, 0, 0) { }

    public static Interpretation Plain(ScanType type) => new(type, 1, 1, false, 0, 0);

    public Interpretation WithScale(int num, int den) => this with { ScaleNum = num, ScaleDen = den };
    public Interpretation WithXor(ulong key) => this with { XorKey = key };
    public Interpretation WithBias(long bias) => this with { Bias = bias };

    public int Width => Type.Width();

    public bool HasScale => ScaleNum != 1 || ScaleDen != 1;
    public bool IsPlain => !HasScale && !BigEndian && XorKey == 0 && Bias == 0;

    /// <summary>True when the encoding destroys numeric ordering, so only point matches are meaningful.</summary>
    public bool PointOnly => BigEndian || XorKey != 0;

    public string Label
    {
        get
        {
            string s = Type.Label();
            if (ScaleNum == 65536 && ScaleDen == 1) return s + " fixed 16.16";
            if (ScaleNum == 256 && ScaleDen == 1) return s + " fixed 24.8";
            if (ScaleDen == 100 && ScaleNum == 1) return s + " ÷100 (percent)";
            if (ScaleNum != 1) s += " ×" + ScaleNum.ToString(CultureInfo.InvariantCulture);
            if (ScaleDen != 1) s += " ÷" + ScaleDen.ToString(CultureInfo.InvariantCulture);
            if (Bias != 0) s += (Bias > 0 ? " +" : " ") + Bias.ToString(CultureInfo.InvariantCulture);
            if (BigEndian) s += " big-endian";
            if (XorKey != 0) s += " ^ 0x" + XorKey.ToString("X", CultureInfo.InvariantCulture);
            return s;
        }
    }

    /// <summary>A one-line hint explaining when this encoding shows up, for the results panel.</summary>
    public string Hint
    {
        get
        {
            if (XorKey != 0) return "Value is obfuscated with a constant XOR key.";
            if (BigEndian) return "Stored with reversed byte order.";
            if (ScaleDen == 100 && ScaleNum == 1) return "Stored as a fraction of full, e.g. 0.75 for 75.";
            if (ScaleNum == 65536 || ScaleNum == 256) return "Fixed-point storage.";
            if (ScaleNum > 1) return $"Stored multiplied by {ScaleNum} to avoid fractions.";
            if (ScaleDen > 1) return $"Stored divided by {ScaleDen}.";
            if (Bias != 0) return "Stored with a constant offset added.";
            return Type.IsFloat() ? "Plain floating point." : "Plain integer.";
        }
    }

    private ulong Encode(ulong bits)
    {
        if (BigEndian) bits = Raw.SwapBytes(Type, bits);
        if (XorKey != 0) bits = (bits ^ XorKey) & Raw.Mask(Type);
        return bits;
    }

    private ulong Undo(ulong bits)
    {
        if (XorKey != 0) bits = (bits ^ XorKey) & Raw.Mask(Type);
        if (BigEndian) bits = Raw.SwapBytes(Type, bits);
        return bits;
    }

    /// <summary>Turns a stored pattern back into the number the player would see.</summary>
    public double Decode(ulong storedBits)
    {
        double numeric = Raw.ToDouble(Type, Undo(storedBits));
        numeric -= Bias;
        if (ScaleNum != 1) numeric /= ScaleNum;
        if (ScaleDen != 1) numeric *= ScaleDen;
        return numeric;
    }

    public string FormatDisplay(ulong storedBits)
    {
        if (IsPlain) return Raw.Format(Type, storedBits);
        double v = Decode(storedBits);
        return v.ToString(Type.IsFloat() || HasScale ? "0.######" : "0", CultureInfo.InvariantCulture);
    }

    public string FormatStored(ulong storedBits) => Raw.Format(Type, storedBits);

    /// <summary>Encodes an exact display value, for writing a new value back into the target.</summary>
    public bool TryEncodeExact(double display, out ulong storedBits)
    {
        storedBits = 0;
        double numeric = display;
        if (ScaleNum != 1) numeric *= ScaleNum;
        if (ScaleDen != 1) numeric /= ScaleDen;
        numeric += Bias;
        if (!Raw.TryFromDouble(Type, numeric, out ulong bits)) return false;
        storedBits = Encode(bits);
        return true;
    }

    /// <summary>
    /// Builds the range of stored patterns that could hold this typed value under this encoding.
    /// Returns false when the value cannot be stored this way at all, which is how impossible
    /// theories get dropped before a single byte is read.
    /// </summary>
    public bool TryEncodeRange(in UserValue value, RoundingMode mode, out ulong loBits, out ulong hiBits)
    {
        loBits = 0;
        hiBits = 0;
        if (!value.IsValid) return false;

        bool plainInteger = !Type.IsFloat() && !HasScale && Bias == 0;
        bool wantPoint = PointOnly || mode == RoundingMode.Exact || (plainInteger && mode != RoundingMode.Loose);

        if (wantPoint)
        {
            if (!TryEncodePoint(value, out ulong bits)) return false;
            loBits = hiBits = bits;
            return true;
        }

        (double lo, double hi) = value.Window(mode);
        lo = ScaleNumeric(lo);
        hi = ScaleNumeric(hi);
        if (lo > hi) (lo, hi) = (hi, lo);

        if (Type.IsFloat())
        {
            double min = Type.MinValue(), max = Type.MaxValue();
            if (hi < min || lo > max) return false;
            lo = Math.Max(lo, min);
            hi = Math.Min(hi, max);

            if (Type == ScanType.Float)
            {
                float flo = MathF.BitDecrement((float)lo);
                float fhi = MathF.BitIncrement((float)hi);
                loBits = BitConverter.SingleToUInt32Bits(flo);
                hiBits = BitConverter.SingleToUInt32Bits(fhi);
            }
            else
            {
                loBits = BitConverter.DoubleToUInt64Bits(lo);
                hiBits = BitConverter.DoubleToUInt64Bits(hi);
            }
            return true;
        }

        double ilo = Math.Ceiling(lo);
        double ihi = Math.Floor(hi);
        if (ilo > ihi) return false;
        if (ihi < Type.MinValue() || ilo > Type.MaxValue()) return false;
        ilo = Math.Max(ilo, Type.MinValue());
        ihi = Math.Min(ihi, Type.MaxValue());

        if (!Raw.TryFromDouble(Type, ilo, out loBits)) return false;
        if (!Raw.TryFromDouble(Type, ihi, out hiBits)) return false;
        return true;
    }

    private double ScaleNumeric(double v)
    {
        if (ScaleNum != 1) v *= ScaleNum;
        if (ScaleDen != 1) v /= ScaleDen;
        return v + Bias;
    }

    private bool TryEncodePoint(in UserValue value, out ulong bits)
    {
        bits = 0;
        if (value.FitsDecimal)
        {
            decimal numeric = value.Dec;
            try
            {
                if (ScaleNum != 1) numeric *= ScaleNum;
                if (ScaleDen != 1) numeric /= ScaleDen;
                numeric += Bias;
            }
            catch (OverflowException)
            {
                return false;
            }

            if (!Raw.TryFromDecimal(Type, numeric, out ulong raw)) return false;
            bits = Encode(raw);
            return true;
        }

        if (!Raw.TryFromDouble(Type, ScaleNumeric(value.Dbl), out ulong rawD)) return false;
        bits = Encode(rawD);
        return true;
    }

    public override string ToString() => Label;
}
