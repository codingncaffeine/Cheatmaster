using System.Globalization;

namespace Cheatmaster.Core.Scanning;

/// <summary>How much slack to allow between the number the player sees and the number in memory.</summary>
public enum RoundingMode
{
    /// <summary>The stored value must equal the typed value exactly.</summary>
    Exact,

    /// <summary>
    /// Allow for a display that rounds or truncates. Typing 83 also matches a stored 82.67,
    /// which is the single most common reason a correct guess appears to find nothing.
    /// </summary>
    Display,

    /// <summary>Allow a couple of percent of drift, for bars and meters that are not exact.</summary>
    Loose
}

/// <summary>A number as the user typed it, kept exact so scaled and integer searches do not lose digits.</summary>
public readonly struct UserValue
{
    public string Text { get; }
    public bool IsValid { get; }
    public bool IsHex { get; }
    public bool FitsDecimal { get; }
    public decimal Dec { get; }
    public double Dbl { get; }
    public int DecimalPlaces { get; }

    private UserValue(string text, bool isValid, bool isHex, bool fitsDecimal, decimal dec, double dbl, int decimalPlaces)
    {
        Text = text;
        IsValid = isValid;
        IsHex = isHex;
        FitsDecimal = fitsDecimal;
        Dec = dec;
        Dbl = dbl;
        DecimalPlaces = decimalPlaces;
    }

    public static readonly UserValue Invalid = new(string.Empty, false, false, false, 0m, 0d, 0);

    public bool IsInteger => FitsDecimal && decimal.Truncate(Dec) == Dec;

    public static UserValue Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Invalid;

        string s = text.Trim();
        if (s.Length == 0) return Invalid;

        bool hex = false;
        string body = s.Replace("_", string.Empty).Replace(" ", string.Empty);

        // The sign comes before the radix prefix, so strip it first: -0x10 is a number people type.
        bool negative = body.StartsWith('-');
        if (negative || body.StartsWith('+')) body = body[1..];

        if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = true;
            body = body[2..];
        }
        else if (body.Length > 1 && (body[^1] == 'h' || body[^1] == 'H') && IsHexDigits(body[..^1]))
        {
            hex = true;
            body = body[..^1];
        }

        if (hex)
        {
            if (body.Length == 0 || body.Length > 16) return Invalid;
            if (!ulong.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong raw)) return Invalid;

            decimal dv = raw;
            if (negative) dv = -dv;
            return new UserValue(text, true, true, true, dv, (double)dv, 0);
        }

        // Numbers arrive the way the user's locale writes them, so try the current culture
        // first, then the invariant form with grouping characters removed.
        var current = CultureInfo.CurrentCulture;
        const NumberStyles Styles = NumberStyles.Float | NumberStyles.AllowThousands;

        if (decimal.TryParse(s, Styles, current, out decimal dec))
            return new UserValue(text, true, false, true, dec, (double)dec, DecimalsIn(s, current.NumberFormat.NumberDecimalSeparator));

        string stripped = s.Replace(",", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
        if (decimal.TryParse(stripped, NumberStyles.Float, CultureInfo.InvariantCulture, out dec))
            return new UserValue(text, true, false, true, dec, (double)dec, DecimalsIn(stripped, "."));

        if (double.TryParse(s, Styles, current, out double dbl) ||
            double.TryParse(stripped, NumberStyles.Float, CultureInfo.InvariantCulture, out dbl))
            return new UserValue(text, true, false, false, 0m, dbl, 0);

        return Invalid;
    }

    private static bool IsHexDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }

    /// <summary>How many digits the user typed after the point, which sets how much slack a match needs.</summary>
    private static int DecimalsIn(string s, string separator)
    {
        if (s.Contains('e') || s.Contains('E')) return 0;
        int at = s.LastIndexOf(separator, StringComparison.Ordinal);
        return at < 0 ? 0 : s.Length - at - separator.Length;
    }

    public static UserValue FromDouble(double value) =>
        new(value.ToString("R", CultureInfo.InvariantCulture), true, false, false, 0m, value, 0);

    /// <summary>The interval of true values that could be displayed as this number.</summary>
    public (double Lo, double Hi) Window(RoundingMode mode)
    {
        double v = FitsDecimal ? (double)Dec : Dbl;
        double ulp = DecimalPlaces <= 0 ? 1.0 : Math.Pow(10, -DecimalPlaces);

        return mode switch
        {
            RoundingMode.Exact => (v, v),
            // Half a step covers round-to-nearest. Truncation needs a full step, and it runs
            // toward zero, so the extra slack belongs above a positive number and below a
            // negative one: -82.67 displays as -82.
            RoundingMode.Display => v >= 0 ? (v - ulp * 0.5, v + ulp) : (v - ulp, v + ulp * 0.5),
            _ => LooseWindow(v, ulp)
        };

        static (double, double) LooseWindow(double v, double ulp)
        {
            double slack = Math.Max(ulp, Math.Abs(v) * 0.02);
            return (v - slack, v + slack);
        }
    }

    public override string ToString() => Text;
}
