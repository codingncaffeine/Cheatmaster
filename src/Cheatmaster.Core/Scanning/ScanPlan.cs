namespace Cheatmaster.Core.Scanning;

/// <summary>How many storage theories to test in one pass.</summary>
public enum ScanProfile
{
    /// <summary>The four encodings that cover most games. Fastest.</summary>
    Fast,

    /// <summary>Every machine type plus the common scaled and percentage encodings. The default.</summary>
    Standard,

    /// <summary>Adds byte-swapped and fixed-point storage. Slower, finds the awkward ones.</summary>
    Thorough
}

/// <summary>One concrete comparison the scanner will run: a type and a range of stored patterns.</summary>
public readonly record struct ScanPlanItem(int InterpId, ScanType Type, ulong LoBits, ulong HiBits)
{
    public bool IsPoint => LoBits == HiBits;
}

/// <summary>Builds the ordered list of storage theories a scan should test.</summary>
public static class InterpretationSets
{
    // Ordered by how often each shows up in practice, because this order drives the results panel.
    private static readonly ScanType[] TypeOrder =
    [
        ScanType.Int32, ScanType.Float, ScanType.Int64, ScanType.Double,
        ScanType.UInt32, ScanType.Int16, ScanType.UInt16, ScanType.UInt64,
        ScanType.Int8, ScanType.UInt8
    ];

    private static readonly ScanType[] FastTypes =
    [
        ScanType.Int32, ScanType.Float, ScanType.Int64, ScanType.Double
    ];

    // Integer types that plausibly hold a value the game multiplied to avoid fractions.
    private static readonly ScanType[] ScalableInts =
    [
        ScanType.Int32, ScanType.Int64, ScanType.UInt32, ScanType.Int16, ScanType.UInt16
    ];

    private static readonly ScanType[] BigEndianTypes =
    [
        ScanType.Int32, ScanType.UInt32, ScanType.Int16, ScanType.UInt16,
        ScanType.Int64, ScanType.UInt64, ScanType.Float, ScanType.Double
    ];

    public static Interpretation[] Build(ScanProfile profile, ScanType? forcedType = null)
    {
        var list = new List<Interpretation>(48);

        if (forcedType is ScanType only)
        {
            list.Add(Interpretation.Plain(only));
            if (profile != ScanProfile.Fast)
            {
                if (!only.IsFloat())
                {
                    list.Add(Interpretation.Plain(only).WithScale(10, 1));
                    list.Add(Interpretation.Plain(only).WithScale(100, 1));
                    list.Add(Interpretation.Plain(only).WithScale(1000, 1));
                }
                else
                {
                    list.Add(Interpretation.Plain(only).WithScale(1, 100));
                }
            }
            if (profile == ScanProfile.Thorough)
            {
                list.Add(Interpretation.Plain(only) with { BigEndian = true });
                if (!only.IsFloat())
                {
                    list.Add(Interpretation.Plain(only).WithScale(65536, 1));
                    list.Add(Interpretation.Plain(only).WithScale(256, 1));
                }
            }
            return Dedupe(list);
        }

        if (profile == ScanProfile.Fast)
        {
            foreach (var t in FastTypes) list.Add(Interpretation.Plain(t));
            return Dedupe(list);
        }

        foreach (var t in TypeOrder) list.Add(Interpretation.Plain(t));

        // Percentage storage: the bar reads 75 but memory holds 0.75.
        list.Add(Interpretation.Plain(ScanType.Float).WithScale(1, 100));
        list.Add(Interpretation.Plain(ScanType.Double).WithScale(1, 100));

        // Multiplied storage: the UI reads 12.5 but memory holds 125 or 1250.
        foreach (var t in ScalableInts)
        {
            list.Add(Interpretation.Plain(t).WithScale(10, 1));
            list.Add(Interpretation.Plain(t).WithScale(100, 1));
            list.Add(Interpretation.Plain(t).WithScale(1000, 1));
        }

        if (profile == ScanProfile.Thorough)
        {
            foreach (var t in BigEndianTypes)
                list.Add(Interpretation.Plain(t) with { BigEndian = true });

            list.Add(Interpretation.Plain(ScanType.Int32).WithScale(65536, 1));
            list.Add(Interpretation.Plain(ScanType.Int32).WithScale(256, 1));
            list.Add(Interpretation.Plain(ScanType.Int64).WithScale(65536, 1));
            list.Add(Interpretation.Plain(ScanType.Float).WithScale(1, 10));
            list.Add(Interpretation.Plain(ScanType.Float).WithScale(1, 1000));
            list.Add(Interpretation.Plain(ScanType.Int32).WithScale(10000, 1));
        }

        return Dedupe(list);
    }

    private static Interpretation[] Dedupe(List<Interpretation> list)
    {
        var seen = new HashSet<Interpretation>();
        var result = new List<Interpretation>(list.Count);
        foreach (var i in list)
        {
            if (seen.Add(i)) result.Add(i);
        }
        return [.. result];
    }
}

/// <summary>The full set of comparisons for one scan, with impossible theories already discarded.</summary>
public sealed class ScanPlan
{
    public required Interpretation[] Interpretations { get; init; }
    public required ScanPlanItem[] Items { get; init; }

    /// <summary>Theories that cannot represent the typed value at all, with the reason.</summary>
    public required int[] RejectedInterpretations { get; init; }

    public int MaxWidth
    {
        get
        {
            int w = 1;
            foreach (var item in Items) w = Math.Max(w, item.Type.Width());
            return w;
        }
    }

    public static ScanPlan Build(Interpretation[] interpretations, in UserValue value, RoundingMode mode) =>
        Build(interpretations, CompareKind.EqualTo, value, UserValue.Invalid, mode, deduplicate: true);

    public static ScanPlan Build(Interpretation[] interpretations, CompareKind compare,
        in UserValue value, in UserValue value2, RoundingMode mode, bool deduplicate = true)
    {
        var items = new List<ScanPlanItem>(interpretations.Length);
        var rejected = new List<int>();
        var seenPoints = new HashSet<(int width, ulong bits)>();
        var seenRanges = new HashSet<(ScanType type, ulong lo, ulong hi)>();

        for (int i = 0; i < interpretations.Length; i++)
        {
            var interp = interpretations[i];
            if (!TryBuildRange(interp, compare, value, value2, mode, out ulong lo, out ulong hi))
            {
                rejected.Add(i);
                continue;
            }

            if (!deduplicate)
            {
                items.Add(new ScanPlanItem(i, interp.Type, lo, hi));
                continue;
            }

            if (lo == hi)
            {
                // Two theories that reduce to the same byte pattern are the same scan.
                if (!seenPoints.Add((interp.Type.Width(), lo)))
                {
                    rejected.Add(i);
                    continue;
                }
            }
            else if (!seenRanges.Add((interp.Type, lo, hi)))
            {
                rejected.Add(i);
                continue;
            }

            items.Add(new ScanPlanItem(i, interp.Type, lo, hi));
        }

        return new ScanPlan
        {
            Interpretations = interpretations,
            Items = [.. items],
            RejectedInterpretations = [.. rejected]
        };
    }

    /// <summary>
    /// Turns a comparison into a closed range of stored patterns. Ordering comparisons are
    /// meaningless once a value has been byte-swapped or XORed, so those theories are dropped
    /// rather than silently producing nonsense.
    /// </summary>
    public static bool TryBuildRange(in Interpretation interp, CompareKind compare,
        in UserValue value, in UserValue value2, RoundingMode mode, out ulong lo, out ulong hi)
    {
        lo = 0;
        hi = 0;
        ScanType t = interp.Type;

        switch (compare)
        {
            case CompareKind.EqualTo:
            case CompareKind.NotEqualTo:
                return interp.TryEncodeRange(value, mode, out lo, out hi);

            case CompareKind.GreaterThan:
                if (interp.PointOnly) return false;
                if (!interp.TryEncodeRange(value, mode, out _, out ulong upper)) return false;
                lo = Raw.NextUp(t, upper);
                hi = Raw.MaxBits(t);
                return Raw.Compare(t, lo, hi) <= 0;

            case CompareKind.LessThan:
                if (interp.PointOnly) return false;
                if (!interp.TryEncodeRange(value, mode, out ulong lower, out _)) return false;
                lo = Raw.MinBits(t);
                hi = Raw.NextDown(t, lower);
                return Raw.Compare(t, lo, hi) <= 0;

            case CompareKind.Between:
                if (interp.PointOnly) return false;
                if (!interp.TryEncodeRange(value, mode, out ulong aLo, out ulong aHi)) return false;
                if (!interp.TryEncodeRange(value2, mode, out ulong bLo, out ulong bHi)) return false;
                lo = Raw.Compare(t, aLo, bLo) <= 0 ? aLo : bLo;
                hi = Raw.Compare(t, aHi, bHi) >= 0 ? aHi : bHi;
                return Raw.Compare(t, lo, hi) <= 0;

            default:
                return false;
        }
    }
}
