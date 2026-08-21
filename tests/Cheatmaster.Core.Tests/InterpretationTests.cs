using System.Globalization;
using Cheatmaster.Core.Scanning;
using Xunit;

namespace Cheatmaster.Core.Tests;

public class UserValueTests
{
    [Theory]
    [InlineData("100", 100.0, 0)]
    [InlineData("12.5", 12.5, 1)]
    [InlineData("-7", -7.0, 0)]
    [InlineData("1,000", 1000.0, 0)]
    [InlineData("0.001", 0.001, 3)]
    public void Parses_decimal_values(string text, double expected, int places)
    {
        var v = UserValue.Parse(text);
        Assert.True(v.IsValid);
        Assert.Equal(expected, (double)v.Dec, 9);
        Assert.Equal(places, v.DecimalPlaces);
    }

    [Theory]
    [InlineData("0x1F", 31.0)]
    [InlineData("FFh", 255.0)]
    public void Parses_hex_values(string text, double expected)
    {
        var v = UserValue.Parse(text);
        Assert.True(v.IsValid);
        Assert.True(v.IsHex);
        Assert.Equal(expected, (double)v.Dec, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    public void Rejects_nonsense(string text) => Assert.False(UserValue.Parse(text).IsValid);

    [Fact]
    public void Display_window_covers_rounding_and_truncation()
    {
        var (lo, hi) = UserValue.Parse("83").Window(RoundingMode.Display);
        Assert.True(lo <= 82.5);   // 82.5 rounds up to 83
        Assert.True(hi >= 83.9);   // 83.9 truncates down to 83
    }

    /// <summary>
    /// Specific cultures must resolve. Turning on InvariantGlobalization breaks this, and WPF
    /// then throws while resolving FrameworkElement.Language before the window even appears.
    /// </summary>
    [Fact]
    public void Specific_cultures_are_available()
    {
        Assert.Equal("en-US", CultureInfo.CreateSpecificCulture("en").Name);
        Assert.Equal("de-DE", new CultureInfo("de-DE").Name);
    }

    [Fact]
    public void Parses_numbers_written_with_a_comma_decimal_separator()
    {
        UserValue parsed = default;
        var worker = new Thread(() =>
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            parsed = UserValue.Parse("12,5");
        });
        worker.Start();
        worker.Join();

        Assert.True(parsed.IsValid);
        Assert.Equal(12.5, (double)parsed.Dec, 9);
        Assert.Equal(1, parsed.DecimalPlaces);
    }

    [Fact]
    public void Exact_window_is_a_point()
    {
        var (lo, hi) = UserValue.Parse("83").Window(RoundingMode.Exact);
        Assert.Equal(83.0, lo);
        Assert.Equal(83.0, hi);
    }
}

public class InterpretationTests
{
    [Fact]
    public void Plain_integer_encodes_to_a_point_even_in_display_mode()
    {
        var interp = Interpretation.Plain(ScanType.Int32);
        Assert.True(interp.TryEncodeRange(UserValue.Parse("100"), RoundingMode.Display, out ulong lo, out ulong hi));
        Assert.Equal(lo, hi);
        Assert.Equal(100u, (uint)lo);
    }

    [Fact]
    public void Float_display_mode_covers_a_truncated_value()
    {
        var interp = Interpretation.Plain(ScanType.Float);
        Assert.True(interp.TryEncodeRange(UserValue.Parse("83"), RoundingMode.Display, out ulong lo, out ulong hi));

        float stored = 82.67f;
        uint bits = BitConverter.SingleToUInt32Bits(stored);
        Assert.True(Raw.Compare(ScanType.Float, bits, lo) >= 0);
        Assert.True(Raw.Compare(ScanType.Float, bits, hi) <= 0);
    }

    [Fact]
    public void Float_exact_mode_does_not_cover_a_truncated_value()
    {
        var interp = Interpretation.Plain(ScanType.Float);
        Assert.True(interp.TryEncodeRange(UserValue.Parse("83"), RoundingMode.Exact, out ulong lo, out ulong hi));

        uint bits = BitConverter.SingleToUInt32Bits(82.67f);
        bool inside = Raw.Compare(ScanType.Float, bits, lo) >= 0 && Raw.Compare(ScanType.Float, bits, hi) <= 0;
        Assert.False(inside);
    }

    [Fact]
    public void Percentage_storage_matches_a_fraction_of_full()
    {
        var interp = Interpretation.Plain(ScanType.Float).WithScale(1, 100);
        Assert.True(interp.TryEncodeRange(UserValue.Parse("75"), RoundingMode.Display, out ulong lo, out ulong hi));

        uint bits = BitConverter.SingleToUInt32Bits(0.75f);
        Assert.True(Raw.Compare(ScanType.Float, bits, lo) >= 0);
        Assert.True(Raw.Compare(ScanType.Float, bits, hi) <= 0);
    }

    [Fact]
    public void Multiplied_storage_matches_a_scaled_integer()
    {
        var interp = Interpretation.Plain(ScanType.Int32).WithScale(100, 1);
        Assert.True(interp.TryEncodeRange(UserValue.Parse("12.5"), RoundingMode.Display, out ulong lo, out ulong hi));
        Assert.True(Raw.Compare(ScanType.Int32, 1250, lo) >= 0);
        Assert.True(Raw.Compare(ScanType.Int32, 1250, hi) <= 0);
    }

    [Fact]
    public void Values_that_do_not_fit_the_type_are_rejected()
    {
        var interp = Interpretation.Plain(ScanType.Int8);
        Assert.False(interp.TryEncodeRange(UserValue.Parse("100000"), RoundingMode.Display, out _, out _));
    }

    [Fact]
    public void Xor_encoding_round_trips()
    {
        var interp = Interpretation.Plain(ScanType.Int32).WithXor(0xDEADBEEF);
        Assert.True(interp.TryEncodeRange(UserValue.Parse("77"), RoundingMode.Exact, out ulong lo, out ulong hi));
        Assert.Equal(lo, hi);
        Assert.Equal(77u ^ 0xDEADBEEF, (uint)lo);
        Assert.Equal(77.0, interp.Decode(lo), 6);
    }

    [Fact]
    public void Big_endian_encoding_round_trips()
    {
        var interp = Interpretation.Plain(ScanType.Int32) with { BigEndian = true };
        Assert.True(interp.TryEncodeRange(UserValue.Parse("77"), RoundingMode.Exact, out ulong lo, out _));
        Assert.Equal(0x4D000000u, (uint)lo);
        Assert.Equal(77.0, interp.Decode(lo), 6);
    }

    [Fact]
    public void Decode_inverts_scaled_storage()
    {
        var interp = Interpretation.Plain(ScanType.Int32).WithScale(100, 1);
        Assert.Equal(12.5, interp.Decode(1250), 6);
    }

    [Fact]
    public void Encode_exact_produces_writable_bits()
    {
        var interp = Interpretation.Plain(ScanType.Float).WithScale(1, 100);
        Assert.True(interp.TryEncodeExact(50, out ulong bits));
        Assert.Equal(0.5f, BitConverter.UInt32BitsToSingle((uint)bits), 5);
    }
}

public class ScanPlanTests
{
    [Fact]
    public void Standard_profile_tests_many_theories_at_once()
    {
        var interps = InterpretationSets.Build(ScanProfile.Standard);
        var plan = ScanPlan.Build(interps, UserValue.Parse("100"), RoundingMode.Display);
        Assert.True(plan.Items.Length >= 8, $"expected a broad plan, got {plan.Items.Length}");
    }

    [Fact]
    public void Identical_byte_patterns_are_only_scanned_once()
    {
        var interps = InterpretationSets.Build(ScanProfile.Standard);
        var plan = ScanPlan.Build(interps, UserValue.Parse("100"), RoundingMode.Display);

        var seen = new HashSet<(int, ulong)>();
        foreach (var item in plan.Items)
        {
            if (!item.IsPoint) continue;
            Assert.True(seen.Add((item.Type.Width(), item.LoBits)), "duplicate point scan in plan");
        }
    }

    [Fact]
    public void Impossible_theories_are_dropped_before_scanning()
    {
        var interps = InterpretationSets.Build(ScanProfile.Standard);
        var plan = ScanPlan.Build(interps, UserValue.Parse("70000"), RoundingMode.Display);

        foreach (var item in plan.Items)
            Assert.True(item.Type.Width() > 2 || interps[item.InterpId].HasScale);
    }

    [Fact]
    public void Greater_than_builds_an_open_ended_range()
    {
        var interp = Interpretation.Plain(ScanType.Int32);
        Assert.True(ScanPlan.TryBuildRange(interp, CompareKind.GreaterThan, UserValue.Parse("10"),
            UserValue.Invalid, RoundingMode.Exact, out ulong lo, out ulong hi));
        Assert.Equal(11, (int)(uint)lo);
        Assert.Equal(int.MaxValue, (int)(uint)hi);
    }

    [Fact]
    public void Ordering_comparisons_reject_scrambled_encodings()
    {
        var interp = Interpretation.Plain(ScanType.Int32).WithXor(0x1234);
        Assert.False(ScanPlan.TryBuildRange(interp, CompareKind.GreaterThan, UserValue.Parse("10"),
            UserValue.Invalid, RoundingMode.Exact, out _, out _));
    }
}
