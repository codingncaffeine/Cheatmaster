using Cheatmaster.Core.Native;

namespace Cheatmaster.Core.Memory;

/// <summary>
/// Which parts of the target address space a scan is allowed to look at.
///
/// The defaults are deliberately wider than the classic tools ship with: excluding
/// mapped or copy-on-write pages is one of the most common reasons a value that is
/// genuinely in memory never shows up in a scan.
/// </summary>
public sealed class RegionFilter
{
    public bool WritableOnly { get; set; } = true;
    public bool SkipExecutable { get; set; }
    public bool IncludeImage { get; set; } = true;
    public bool IncludePrivate { get; set; } = true;
    public bool IncludeMapped { get; set; } = true;

    public ulong Start { get; set; }
    public ulong End { get; set; } = 0x7FFF_FFFF_FFFF;

    /// <summary>Skip regions larger than this. 0 disables the cap. Very large mapped views are usually file caches.</summary>
    public ulong MaxRegionSize { get; set; }

    public static RegionFilter Default => new();

    /// <summary>Nothing excluded except pages that cannot be read at all.</summary>
    public static RegionFilter Everything => new()
    {
        WritableOnly = false,
        SkipExecutable = false,
        IncludeImage = true,
        IncludePrivate = true,
        IncludeMapped = true
    };

    public RegionFilter Clone() => (RegionFilter)MemberwiseClone();

    public bool Matches(in MemoryRegion r)
    {
        if (r.Size == 0) return false;
        if (r.IsGuarded) return false;
        if ((r.Protect & PageProtect.AccessMask) == PageProtect.NoAccess) return false;
        if (WritableOnly && !r.IsWritable) return false;
        if (SkipExecutable && r.IsExecutable) return false;
        if (MaxRegionSize != 0 && r.Size > MaxRegionSize) return false;

        if (r.IsImage && !IncludeImage) return false;
        if (r.IsMapped && !IncludeMapped) return false;
        if (r.IsPrivate && !IncludePrivate) return false;

        if (r.End <= Start || r.Base >= End) return false;
        return true;
    }

    /// <summary>Clamps a matching region to the configured address window.</summary>
    public MemoryRegion Clamp(in MemoryRegion r)
    {
        ulong lo = Math.Max(r.Base, Start);
        ulong hi = Math.Min(r.End, End);
        return r with { Base = lo, Size = hi - lo };
    }
}
