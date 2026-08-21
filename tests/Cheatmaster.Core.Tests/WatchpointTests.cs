using Cheatmaster.Core.Debugging;
using Xunit;

namespace Cheatmaster.Core.Tests;

/// <summary>
/// The arithmetic behind the processor's four watchpoints, and the trick that reads an object
/// pointer out of the register file instead of decoding the instruction.
///
/// None of this needs a process to attach to, which matters: a wrong length encoding or a
/// misread DR6 produces a watch that silently never fires, and that is indistinguishable from a
/// game that simply never touched the address.
/// </summary>
public class WatchpointTests
{
    [Fact]
    public void An_aligned_value_takes_a_single_watchpoint()
    {
        var slots = DebugRegisters.Plan(0x1000, 4);

        Assert.Single(slots);
        Assert.Equal(new WatchSlot(0x1000, 4), slots[0]);
        Assert.True(DebugRegisters.Covers(slots, 0x1000, 4));
    }

    /// <summary>
    /// The hardware can only watch a span aligned to its own length, so a value that straddles
    /// an alignment boundary has to be covered piece by piece.
    /// </summary>
    [Fact]
    public void An_unaligned_value_is_split_into_aligned_spans()
    {
        var slots = DebugRegisters.Plan(0x1003, 8);

        Assert.True(DebugRegisters.Covers(slots, 0x1003, 8), "the split left part of the value unwatched");
        Assert.True(slots.Count <= DebugRegisters.SlotCount, "more watchpoints than the processor has");
        foreach (var slot in slots)
            Assert.True(slot.Address % (ulong)slot.Length == 0, $"{slot} is not aligned to its own length");
    }

    /// <summary>
    /// A 32-bit target runs in compatibility mode, where the eight-byte length is not dependable.
    /// Asking for it there produces a watch that never fires.
    /// </summary>
    [Fact]
    public void A_32_bit_target_never_gets_an_eight_byte_span()
    {
        var slots = DebugRegisters.Plan(0x2000, 8, maxChunk: 4);

        Assert.All(slots, slot => Assert.True(slot.Length <= 4));
        Assert.True(DebugRegisters.Covers(slots, 0x2000, 8));
    }

    /// <summary>
    /// The length field is not in numeric order: eight bytes is 10b and four bytes is 11b. Read
    /// it as a size and every four-byte watch becomes an eight-byte one.
    /// </summary>
    [Fact]
    public void The_length_encoding_puts_eight_bytes_out_of_order()
    {
        Assert.Equal(0b00u, DebugRegisters.LengthBits(1));
        Assert.Equal(0b01u, DebugRegisters.LengthBits(2));
        Assert.Equal(0b10u, DebugRegisters.LengthBits(8));
        Assert.Equal(0b11u, DebugRegisters.LengthBits(4));

        foreach (int length in new[] { 1, 2, 4, 8 })
            Assert.Equal(length, DebugRegisters.LengthFromBits(DebugRegisters.LengthBits(length)));
    }

    [Fact]
    public void The_control_register_says_back_exactly_what_was_asked_for()
    {
        // Two slots, so the two left over can be checked for staying switched off.
        var slots = DebugRegisters.Plan(0x1002, 4);
        Assert.Equal(2, slots.Count);

        ulong dr7 = DebugRegisters.Control(slots, WatchOn.ReadOrWrite);

        for (int i = 0; i < slots.Count; i++)
        {
            Assert.True(DebugRegisters.IsEnabled(dr7, i), $"slot {i} was not enabled");
            Assert.Equal(0b11u, DebugRegisters.AccessOf(dr7, i));
            Assert.Equal(slots[i].Length, DebugRegisters.LengthOf(dr7, i));
        }

        for (int i = slots.Count; i < DebugRegisters.SlotCount; i++)
            Assert.False(DebugRegisters.IsEnabled(dr7, i), $"slot {i} was enabled without being asked for");

        // Bit 10 is reserved and must be written as one.
        Assert.NotEqual(0UL, dr7 & (1UL << 10));
    }

    [Fact]
    public void Watching_for_writes_only_is_a_different_encoding_from_watching_for_reads()
    {
        var slots = DebugRegisters.Plan(0x1000, 4);

        Assert.Equal(0b01u, DebugRegisters.AccessOf(DebugRegisters.Control(slots, WatchOn.Write), 0));
        Assert.Equal(0b11u, DebugRegisters.AccessOf(DebugRegisters.Control(slots, WatchOn.ReadOrWrite), 0));
    }

    [Fact]
    public void Dr6_names_the_watchpoint_that_fired()
    {
        Assert.Equal(0, DebugRegisters.FiredSlot(0b0001));
        Assert.Equal(2, DebugRegisters.FiredSlot(0b0100));

        // Bit 14 is a plain single step, which belongs to whoever else is debugging.
        Assert.Equal(-1, DebugRegisters.FiredSlot(1UL << 14));
        Assert.Equal(-1, DebugRegisters.FiredSlot(0));
    }

    [Fact]
    public void The_register_holding_the_object_is_the_one_just_below_the_value()
    {
        RegisterValue[] registers =
        [
            new("RAX", 0x1234),
            new("RBX", 0x140000000),        // the object
            new("RCX", 0x140000018),        // the field itself
            new("RDX", 0x7FFFFFFFFFFF)      // above the address, so not a base
        ];

        var candidates = BaseRanker.Candidates(registers, 0x140000018, 0x1000);

        Assert.Equal(("RCX", 0), candidates[0]);
        Assert.Contains(("RBX", 0x18), candidates);
        Assert.DoesNotContain(candidates, c => c.Register is "RAX" or "RDX");
    }

    [Fact]
    public void A_register_too_far_below_the_value_is_not_the_object()
    {
        RegisterValue[] registers = [new("RBX", 0x140000000)];

        Assert.Empty(BaseRanker.Candidates(registers, 0x140000000 + 0x2000, 0x1000));
        Assert.Single(BaseRanker.Candidates(registers, 0x140000000 + 0x800, 0x1000));
    }

    /// <summary>
    /// One hit is a coincidence waiting to happen: any register can hold a nearby number once.
    /// A register that lands on the same offset every single time is the object.
    /// </summary>
    [Fact]
    public void A_register_that_matches_every_hit_outranks_one_that_matched_once()
    {
        const ulong field = 0x140000018;
        var ranker = new BaseRanker(field, 0x1000);

        ranker.Add([new RegisterValue("RBX", 0x140000000), new RegisterValue("RSI", 0x140000010)]);
        ranker.Add([new RegisterValue("RBX", 0x140000000), new RegisterValue("RSI", 0x99999999)]);
        ranker.Add([new RegisterValue("RBX", 0x140000000), new RegisterValue("RSI", 0x99999999)]);

        var ranked = ranker.Ranked();

        Assert.Equal("RBX", ranked[0].Register);
        Assert.Equal(0x18, ranked[0].Offset);
        Assert.Equal(0x140000000UL, ranked[0].Value);
        Assert.Equal(3, ranked[0].Hits);
        Assert.Equal("[RBX+18]", ranked[0].Display);
    }

    /// <summary>
    /// A stack-relative hit is true and useless: it says the value is a local, which leads
    /// nowhere as a saved route. A real object pointer wins even when both matched every hit.
    /// </summary>
    [Fact]
    public void A_real_object_pointer_outranks_the_stack()
    {
        const ulong field = 0x140000018;
        var ranker = new BaseRanker(field, 0x1000);

        for (int i = 0; i < 4; i++)
            ranker.Add([new RegisterValue("RSP", 0x140000008), new RegisterValue("R12", 0x140000000)]);

        var ranked = ranker.Ranked();

        Assert.Equal("R12", ranked[0].Register);
        Assert.False(ranked[0].IsStack);
        Assert.Contains(ranked, guess => guess.Register == "RSP" && guess.IsStack);
    }
}
