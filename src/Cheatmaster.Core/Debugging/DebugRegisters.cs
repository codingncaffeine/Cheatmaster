namespace Cheatmaster.Core.Debugging;

/// <summary>One hardware watchpoint: an aligned span of memory the processor traps on.</summary>
public readonly record struct WatchSlot(ulong Address, int Length)
{
    public override string ToString() => $"{Address:X}+{Length}";
}

/// <summary>What kind of access trips a watchpoint.</summary>
public enum WatchOn
{
    /// <summary>Writes only. Cheaper on a busy address, but misses the code that reads it.</summary>
    Write,

    /// <summary>Reads and writes. There is no read-only encoding in the hardware.</summary>
    ReadOrWrite
}

/// <summary>
/// The processor's four hardware watchpoints, encoded.
///
/// DR0-DR3 hold the addresses, DR7 says which are live and what trips them, and DR6 says which
/// one just fired. Every field here is arithmetic on those registers and nothing else, so it can
/// be checked without a process to attach to.
///
/// Two constraints shape everything: an address must be aligned to the length being watched, and
/// the length can only be 1, 2, 4 or 8 bytes. A value that is neither aligned nor a legal length
/// therefore has to be covered by several slots — an unaligned 8-byte value needs all four.
/// </summary>
public static class DebugRegisters
{
    public const int SlotCount = 4;

    /// <summary>Bit 10 of DR7 is reserved and must be written as one.</summary>
    private const ulong Reserved = 1UL << 10;

    /// <summary>
    /// Splits a value into aligned watchable spans, largest first. Returns fewer spans than the
    /// value needs only when four slots cannot cover it, which the caller must report rather
    /// than pretend to watch.
    /// </summary>
    public static List<WatchSlot> Plan(ulong address, int width, int maxChunk = 8)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        var slots = new List<WatchSlot>(SlotCount);
        ulong end = address + (ulong)width;
        ulong at = address;

        while (at < end && slots.Count < SlotCount)
        {
            int chunk = maxChunk;
            while (chunk > 1 && (at % (ulong)chunk != 0 || at + (ulong)chunk > end))
                chunk /= 2;

            slots.Add(new WatchSlot(at, chunk));
            at += (ulong)chunk;
        }

        return slots;
    }

    /// <summary>True when the plan covers every byte of the value.</summary>
    public static bool Covers(IReadOnlyList<WatchSlot> slots, ulong address, int width)
    {
        ulong at = address;
        foreach (var slot in slots)
        {
            if (slot.Address != at) return false;
            at += (ulong)slot.Length;
        }
        return at == address + (ulong)width;
    }

    /// <summary>The two-bit length encoding. 8 bytes is 10b, which is out of numeric order.</summary>
    public static uint LengthBits(int length) => length switch
    {
        1 => 0b00,
        2 => 0b01,
        8 => 0b10,
        4 => 0b11,
        _ => throw new ArgumentOutOfRangeException(nameof(length), length, "A watchpoint can only cover 1, 2, 4 or 8 bytes.")
    };

    public static int LengthFromBits(uint bits) => bits switch
    {
        0b00 => 1,
        0b01 => 2,
        0b10 => 8,
        _ => 4
    };

    /// <summary>Execute is 00, write is 01, and 11 is read or write. There is no read-only value.</summary>
    public static uint AccessBits(WatchOn on) => on == WatchOn.Write ? 0b01u : 0b11u;

    /// <summary>Builds the DR7 that enables exactly these slots.</summary>
    public static ulong Control(IReadOnlyList<WatchSlot> slots, WatchOn on)
    {
        ulong dr7 = Reserved;
        uint rw = AccessBits(on);

        for (int i = 0; i < slots.Count && i < SlotCount; i++)
        {
            dr7 |= 1UL << (i * 2);                                   // local enable
            dr7 |= (ulong)rw << (16 + i * 4);
            dr7 |= (ulong)LengthBits(slots[i].Length) << (18 + i * 4);
        }

        return dr7;
    }

    public static bool IsEnabled(ulong dr7, int slot) => (dr7 & (3UL << (slot * 2))) != 0;

    public static uint AccessOf(ulong dr7, int slot) => (uint)((dr7 >> (16 + slot * 4)) & 3);

    public static int LengthOf(ulong dr7, int slot) => LengthFromBits((uint)((dr7 >> (18 + slot * 4)) & 3));

    /// <summary>
    /// Which watchpoint the processor is reporting, or -1 when the trap was somebody else's
    /// single step. DR6 is sticky: it must be cleared or the same bits read back forever.
    /// </summary>
    public static int FiredSlot(ulong dr6)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if ((dr6 & (1UL << i)) != 0) return i;
        }
        return -1;
    }
}
