using Cheatmaster.Core.Cheats;
using Cheatmaster.Core.Memory;

namespace Cheatmaster.Core.Scanning;

/// <summary>A route to a value that starts somewhere fixed and therefore survives a restart.</summary>
public sealed record PointerPath(string Module, ulong ModuleOffset, int[] Offsets)
{
    public int Depth => Offsets.Length;

    public string Display
    {
        get
        {
            string head = $"\"{Module}\"+{ModuleOffset:X}";
            if (Offsets.Length == 0) return head;
            return $"[{head}] + " + string.Join(" + ", Offsets.Select(static o => o.ToString("X")));
        }
    }

    public AddressSpec ToAddressSpec() => new()
    {
        Module = Module,
        Offset = ModuleOffset,
        Pointers = Offsets
    };
}

public sealed class PointerScanOptions
{
    /// <summary>How far into a structure a field may sit. Larger finds more and costs more.</summary>
    public int MaxOffset { get; set; } = 0x800;

    /// <summary>How many pointers deep to follow before giving up on a route.</summary>
    public int MaxDepth { get; set; } = 5;

    public int MaxResults { get; set; } = 200;

    /// <summary>Guards against a runaway search in a target with millions of self-referencing pointers.</summary>
    public int MaxNodesVisited { get; set; } = 2_000_000;

    /// <summary>
    /// The offset from the object to the value, when watching the game's code has already
    /// established it.
    ///
    /// Blind, the last step of a route has to accept a pointer landing anywhere within a
    /// structure's worth of the target, which is where most of the noise in a pointer scan comes
    /// from. Having watched an instruction reach the value as <c>[RBX+18]</c>, that offset is
    /// known exactly, and every route that arrives some other way can be discarded on sight.
    /// </summary>
    public int? FinalOffset { get; set; }
}

/// <summary>
/// Finds a stable route to an address that moves.
///
/// A value on the heap is at a new address every launch, which is why a saved raw address is
/// worth nothing the next day. What does not move is the path: some field of some object, reached
/// from a pointer that lives at a fixed offset inside the executable. Walking backwards from the
/// address — what points here, and what points at that — until the trail reaches a module gives a
/// route that resolves correctly every time the game runs.
/// </summary>
public static class PointerScanner
{
    public static List<PointerPath> Find(TargetProcess process, PointerMap map, ulong target,
        PointerScanOptions options, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var modules = process.Modules;
        if (modules.Count == 0) throw new ScanException("The target reported no modules to anchor a path to.");

        var found = new List<PointerPath>();
        var visited = new HashSet<ulong>();
        var candidates = new List<(ulong Source, int Offset)>();
        int nodes = 0;

        // Breadth first, so the shortest and most reliable routes surface before the deep ones.
        var queue = new Queue<(ulong Address, int[] Offsets)>();
        queue.Enqueue((target, []));

        while (queue.Count > 0 && found.Count < options.MaxResults && nodes < options.MaxNodesVisited)
        {
            ct.ThrowIfCancellationRequested();
            var (address, offsets) = queue.Dequeue();
            nodes++;

            if ((nodes & 8191) == 0)
                progress?.Report(new ScanProgress("Tracing pointers", nodes, options.MaxNodesVisited, found.Count));

            // A known field offset can sit further into the object than the search would otherwise
            // look, and refusing to look that far would rule out the one route we already know exists.
            int reach = offsets.Length == 0 && options.FinalOffset is { } known
                ? Math.Max(options.MaxOffset, known)
                : options.MaxOffset;

            map.FindPointersTo(address, reach, candidates);

            foreach (var (source, offset) in candidates)
            {
                if (found.Count >= options.MaxResults) break;

                // The first step out from the target is the last one a route applies, so a known
                // field offset constrains exactly this one.
                if (offsets.Length == 0 && options.FinalOffset is { } required && offset != required)
                    continue;

                var chain = Prepend(offset, offsets);

                // A pointer living inside a module is the fixed ground we were looking for.
                var module = ModuleHolding(modules, source);
                if (module is not null)
                {
                    found.Add(new PointerPath(module.Name, source - module.Base, chain));
                    continue;
                }

                if (chain.Length >= options.MaxDepth) continue;
                if (!visited.Add(source)) continue;

                queue.Enqueue((source, chain));
            }
        }

        // Shorter routes first: fewer hops means fewer ways for it to break.
        found.Sort(static (a, b) => a.Depth.CompareTo(b.Depth));
        progress?.Report(new ScanProgress("Tracing pointers", options.MaxNodesVisited, options.MaxNodesVisited, found.Count));
        return found;
    }

    private static int[] Prepend(int offset, int[] rest)
    {
        var chain = new int[rest.Length + 1];
        chain[0] = offset;
        Array.Copy(rest, 0, chain, 1, rest.Length);
        return chain;
    }

    private static ModuleEntry? ModuleHolding(IReadOnlyList<ModuleEntry> modules, ulong address)
    {
        foreach (var module in modules)
        {
            if (address >= module.Base && address < module.End) return module;
        }
        return null;
    }

    /// <summary>
    /// Keeps only the routes that still land on the expected value. Run after a restart, this is
    /// what separates a path that genuinely holds from one that happened to work once.
    /// </summary>
    public static List<PointerPath> Verify(TargetProcess process, IEnumerable<PointerPath> paths,
        ScanType type, ulong expectedBits, CancellationToken ct = default)
    {
        var survivors = new List<PointerPath>();
        Span<byte> buffer = stackalloc byte[8];
        int width = type.Width();

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            ulong address = path.ToAddressSpec().Resolve(process);
            if (address == 0) continue;
            if (!process.ReadExact(address, buffer[..width])) continue;
            if (Raw.ReadBits(type, buffer) != expectedBits) continue;

            survivors.Add(path);
        }

        return survivors;
    }
}
