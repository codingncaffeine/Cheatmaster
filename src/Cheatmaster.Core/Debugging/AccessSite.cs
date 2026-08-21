using System.Globalization;

namespace Cheatmaster.Core.Debugging;

/// <summary>
/// What the instruction did. The hardware cannot tell reads from writes, so this is decided by
/// whether the value actually changed — a write that stores the same value reads as a read.
/// </summary>
public enum AccessKind { Read, Write }

public readonly record struct RegisterValue(string Name, ulong Value);

/// <summary>
/// A register that sits at or just below the accessed address, and therefore looks like the
/// object the value belongs to.
/// </summary>
public sealed record BaseGuess(string Register, ulong Value, int Offset, bool IsStack, int Hits)
{
    public string Display => Offset == 0
        ? $"[{Register}]"
        : $"[{Register}+{Offset.ToString("X", CultureInfo.InvariantCulture)}]";

    public override string ToString() => Display;
}

/// <summary>One trap: the state of the machine at the instruction after the access.</summary>
public sealed class AccessHit
{
    public required ulong InstructionPointer { get; init; }
    public required int ThreadId { get; init; }
    public required AccessKind Kind { get; init; }

    /// <summary>The watched address that fired, which for a split value may not be its first byte.</summary>
    public required ulong Address { get; init; }

    public required ulong Value { get; init; }
    public required IReadOnlyList<RegisterValue> Registers { get; init; }

    /// <summary>Bytes read around the instruction pointer, for someone who wants to read the code.</summary>
    public required byte[] Code { get; init; }

    public required ulong CodeBase { get; init; }
}

/// <summary>Everything learned about one instruction that touches the watched address.</summary>
public sealed class AccessSite
{
    public required ulong InstructionPointer { get; init; }

    /// <summary>Where the instruction lives, when it is inside a loaded module rather than generated code.</summary>
    public string? Module { get; init; }

    public ulong ModuleOffset { get; init; }

    public required int Reads { get; init; }
    public required int Writes { get; init; }
    public int Count => Reads + Writes;

    public required AccessHit Latest { get; init; }

    /// <summary>Every register that could be the object base, best first.</summary>
    public required IReadOnlyList<BaseGuess> Bases { get; init; }

    public BaseGuess? Base => Bases.Count > 0 ? Bases[0] : null;

    public string Location => Module is null
        ? InstructionPointer.ToString("X", CultureInfo.InvariantCulture)
        : $"{Module}+{ModuleOffset.ToString("X", CultureInfo.InvariantCulture)}";

    public string Summary
    {
        get
        {
            string what = Writes > 0 && Reads > 0 ? "reads and writes"
                : Writes > 0 ? "writes"
                : "reads";
            string basis = Base is null ? "" : $" as {Base.Display}";
            return $"{what} it{basis}";
        }
    }
}

/// <summary>
/// Works out which register the address was reached through, without decoding the instruction.
///
/// Decoding x86-64 backwards from the address after an instruction is genuinely hard, and it is
/// not needed: at the moment of the access the object pointer is still sitting in a register. A
/// register holding <c>target - 0x18</c> means the instruction was <c>[REG+18]</c>, and REG is
/// the object — which is exactly what a pointer route needs, handed over rather than searched for.
///
/// A single hit can produce a coincidence. Counting how often each register lands on the same
/// offset across many hits separates the real base from a register that happened to hold a
/// nearby number once.
/// </summary>
public sealed class BaseRanker
{
    private static readonly HashSet<string> StackRegisters =
        new(StringComparer.OrdinalIgnoreCase) { "RSP", "RBP", "ESP", "EBP" };

    private readonly Dictionary<(string Register, int Offset), int> _counts = [];
    private readonly ulong _address;
    private readonly int _maxOffset;
    private int _hits;

    /// <param name="address">The first byte of the value being watched, which every offset is measured from.</param>
    /// <param name="maxOffset">How far into a structure a field is allowed to sit.</param>
    public BaseRanker(ulong address, int maxOffset)
    {
        _address = address;
        _maxOffset = maxOffset;
    }

    public int Hits => _hits;

    /// <summary>Registers at or just below the address, nearest first.</summary>
    public static List<(string Register, int Offset)> Candidates(
        IReadOnlyList<RegisterValue> registers, ulong address, int maxOffset)
    {
        var found = new List<(string Register, int Offset)>();
        foreach (var (name, value) in registers)
        {
            if (value == 0 || value > address) continue;
            ulong delta = address - value;
            if (delta > (ulong)maxOffset) continue;
            found.Add((name, (int)delta));
        }

        found.Sort(static (a, b) => a.Offset != b.Offset
            ? a.Offset.CompareTo(b.Offset)
            : string.CompareOrdinal(a.Register, b.Register));
        return found;
    }

    public void Add(IReadOnlyList<RegisterValue> registers)
    {
        _hits++;
        foreach (var (register, offset) in Candidates(registers, _address, _maxOffset))
        {
            _counts.TryGetValue((register, offset), out int count);
            _counts[(register, offset)] = count + 1;
        }
    }

    /// <summary>
    /// Best first. A register that held the same offset at every single hit outranks one that
    /// only sometimes did, and a genuine object pointer outranks the stack — a stack-relative
    /// hit means a local variable, which is true but leads nowhere as a saved route.
    /// </summary>
    public List<BaseGuess> Ranked()
    {
        var guesses = new List<BaseGuess>(_counts.Count);
        foreach (var ((register, offset), count) in _counts)
        {
            guesses.Add(new BaseGuess(
                register,
                _address - (ulong)offset,
                offset,
                StackRegisters.Contains(register),
                count));
        }

        int hits = _hits;
        guesses.Sort((a, b) =>
        {
            bool aEvery = a.Hits == hits, bEvery = b.Hits == hits;
            if (aEvery != bEvery) return aEvery ? -1 : 1;
            if (a.IsStack != b.IsStack) return a.IsStack ? 1 : -1;
            if (a.Hits != b.Hits) return b.Hits.CompareTo(a.Hits);
            if (a.Offset != b.Offset) return a.Offset.CompareTo(b.Offset);
            return string.CompareOrdinal(a.Register, b.Register);
        });

        return guesses;
    }
}
