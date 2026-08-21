namespace Cheatmaster.Core.Scanning;

public enum CompareKind
{
    EqualTo,
    NotEqualTo,
    GreaterThan,
    LessThan,
    Between,

    /// <summary>First scan only: record the whole searched area so later scans can compare against it.</summary>
    UnknownInitialValue,

    Changed,
    Unchanged,
    Increased,
    Decreased,
    IncreasedBy,
    DecreasedBy,

    /// <summary>
    /// Was A, is now B. Matches plain storage and also recovers constant XOR keys and offsets,
    /// which is how obfuscated values get found without knowing the key.
    /// </summary>
    ChangedFromTo
}

public static class CompareKinds
{
    public static bool NeedsValue(this CompareKind k) =>
        k is CompareKind.EqualTo or CompareKind.NotEqualTo or CompareKind.GreaterThan
          or CompareKind.LessThan or CompareKind.Between or CompareKind.IncreasedBy
          or CompareKind.DecreasedBy or CompareKind.ChangedFromTo;

    public static bool NeedsSecondValue(this CompareKind k) =>
        k is CompareKind.Between or CompareKind.ChangedFromTo;

    public static bool NeedsPrevious(this CompareKind k) =>
        k is CompareKind.Changed or CompareKind.Unchanged or CompareKind.Increased
          or CompareKind.Decreased or CompareKind.IncreasedBy or CompareKind.DecreasedBy
          or CompareKind.ChangedFromTo;

    public static bool IsRangeComparison(this CompareKind k) =>
        k is CompareKind.EqualTo or CompareKind.NotEqualTo or CompareKind.GreaterThan
          or CompareKind.LessThan or CompareKind.Between;

    public static string Label(this CompareKind k) => k switch
    {
        CompareKind.EqualTo => "Equal to",
        CompareKind.NotEqualTo => "Not equal to",
        CompareKind.GreaterThan => "Greater than",
        CompareKind.LessThan => "Less than",
        CompareKind.Between => "Between",
        CompareKind.UnknownInitialValue => "Unknown initial value",
        CompareKind.Changed => "Changed",
        CompareKind.Unchanged => "Unchanged",
        CompareKind.Increased => "Increased",
        CompareKind.Decreased => "Decreased",
        CompareKind.IncreasedBy => "Increased by",
        CompareKind.DecreasedBy => "Decreased by",
        _ => "Was … now …"
    };
}

public sealed class ScanSettings
{
    /// <summary>Candidate addresses must be a multiple of this. 4 is the usual compromise; 1 finds everything.</summary>
    public int Alignment { get; set; } = 4;

    public int MaxResults { get; set; } = 20_000_000;

    /// <summary>A theory that matches more addresses than this is noise, and stops being collected.</summary>
    public int MaxResultsPerInterpretation { get; set; } = 2_000_000;

    public int ChunkSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>0 uses every core.</summary>
    public int WorkerCount { get; set; }

    /// <summary>Upper bound on a memory snapshot, in bytes. Snapshots are what make unknown-value scans work.</summary>
    public long SnapshotBudget { get; set; } = 3L * 1024 * 1024 * 1024;

    public RegionFilterSettings Regions { get; set; } = new();

    public ScanSettings Clone()
    {
        var c = (ScanSettings)MemberwiseClone();
        c.Regions = Regions.Clone();
        return c;
    }
}

/// <summary>Serialisable mirror of the region filter, so settings can round-trip to disk.</summary>
public sealed class RegionFilterSettings
{
    public bool WritableOnly { get; set; } = true;
    public bool SkipExecutable { get; set; }
    public bool IncludeImage { get; set; } = true;
    public bool IncludePrivate { get; set; } = true;
    public bool IncludeMapped { get; set; } = true;
    public ulong Start { get; set; }
    public ulong End { get; set; } = 0x7FFF_FFFF_FFFF;

    public Memory.RegionFilter ToFilter() => new()
    {
        WritableOnly = WritableOnly,
        SkipExecutable = SkipExecutable,
        IncludeImage = IncludeImage,
        IncludePrivate = IncludePrivate,
        IncludeMapped = IncludeMapped,
        Start = Start,
        End = End
    };

    public RegionFilterSettings Clone() => (RegionFilterSettings)MemberwiseClone();
}

public sealed class ScanRequest
{
    public CompareKind Compare { get; init; } = CompareKind.EqualTo;
    public UserValue Value { get; init; } = UserValue.Invalid;
    public UserValue Value2 { get; init; } = UserValue.Invalid;
    public ScanProfile Profile { get; init; } = ScanProfile.Standard;

    /// <summary>Null means let the scanner work the type out.</summary>
    public ScanType? ForcedType { get; init; }

    public RoundingMode Rounding { get; init; } = RoundingMode.Display;

    /// <summary>When set, only these interpretation ids survive. Used once a theory has been pinned.</summary>
    public int[]? RestrictToInterpretations { get; init; }
}
