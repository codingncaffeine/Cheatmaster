namespace Cheatmaster.Core.Scanning;

/// <summary>How many hits one storage theory produced, and whether it is still worth showing.</summary>
public sealed record InterpretationGroup(int InterpId, Interpretation Interpretation, int Count, bool Capped)
{
    public string Label => Interpretation.Label;
    public string Hint => Interpretation.Hint;
}

/// <summary>The surviving addresses of a scan, with the theory each one was found under.</summary>
public sealed class ScanResults
{
    public required Interpretation[] Interpretations { get; init; }
    public required ulong[] Addresses { get; init; }
    public required int[] InterpIds { get; init; }
    public required ulong[] Values { get; init; }
    public required int Count { get; init; }
    public required IReadOnlyList<InterpretationGroup> Groups { get; init; }

    public bool Truncated { get; init; }
    public long BytesScanned { get; init; }
    public TimeSpan Duration { get; init; }

    public static ScanResults Empty(Interpretation[] interpretations) => new()
    {
        Interpretations = interpretations,
        Addresses = [],
        InterpIds = [],
        Values = [],
        Count = 0,
        Groups = []
    };

    public Interpretation InterpretationAt(int index) => Interpretations[InterpIds[index]];

    /// <summary>
    /// The theory the evidence points at: the surviving group with the fewest hits, since a
    /// wrong theory that still matches usually matches far more addresses than the right one.
    /// </summary>
    public InterpretationGroup? BestGuess
    {
        get
        {
            InterpretationGroup? best = null;
            foreach (var g in Groups)
            {
                if (g.Count == 0 || g.Capped) continue;
                if (best is null || g.Count < best.Count) best = g;
            }
            return best;
        }
    }

    /// <summary>
    /// Keeps only the hits found under the given theories. Used when the user picks one of the
    /// surviving encodings, which needs no rescan because the evidence is already in hand.
    /// </summary>
    public ScanResults FilterTo(IReadOnlyCollection<int> interpIds)
    {
        var keep = new HashSet<int>(interpIds);
        int count = 0;
        for (int i = 0; i < Count; i++)
        {
            if (keep.Contains(InterpIds[i])) count++;
        }

        var addresses = new ulong[count];
        var ids = new int[count];
        var values = new ulong[count];
        var counts = new int[Interpretations.Length];

        int pos = 0;
        for (int i = 0; i < Count; i++)
        {
            int id = InterpIds[i];
            if (!keep.Contains(id)) continue;
            addresses[pos] = Addresses[i];
            ids[pos] = id;
            values[pos] = Values[i];
            counts[id]++;
            pos++;
        }

        var groups = new List<InterpretationGroup>();
        for (int i = 0; i < Interpretations.Length; i++)
        {
            if (counts[i] > 0) groups.Add(new InterpretationGroup(i, Interpretations[i], counts[i], false));
        }

        return new ScanResults
        {
            Interpretations = Interpretations,
            Addresses = addresses,
            InterpIds = ids,
            Values = values,
            Count = count,
            Groups = groups,
            Truncated = Truncated,
            BytesScanned = BytesScanned,
            Duration = Duration
        };
    }

    public IReadOnlyList<InterpretationGroup> RankedGroups
    {
        get
        {
            var list = new List<InterpretationGroup>(Groups);
            list.Sort(static (a, b) =>
            {
                if (a.Capped != b.Capped) return a.Capped ? 1 : -1;
                return a.Count.CompareTo(b.Count);
            });
            return list;
        }
    }
}

public readonly record struct ScanProgress(string Phase, long BytesDone, long BytesTotal, long Found)
{
    public double Fraction => BytesTotal <= 0 ? 0 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
}
