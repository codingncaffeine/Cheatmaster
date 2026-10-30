using System.Globalization;
using Avalonia.Data.Converters;

namespace Cheatmaster.App.Infrastructure;

/// <summary>True when there is something to show: text that is not blank, a count above zero, a non-empty list.</summary>
public sealed class NotEmpty : IValueConverter
{
    public static readonly NotEmpty Instance = new();
    public static readonly NotEmpty Inverted = new() { Invert = true };

    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i > 0,
            long l => l > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true
        };
        return Invert ? !has : has;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public static readonly InverseBool Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;
}

/// <summary>Formats a byte count for the status bar.</summary>
public sealed class ByteSize : IValueConverter
{
    public static readonly ByteSize Instance = new();

    public static string Format(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return bytes.ToString(bytes >= 100 || unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? string.Empty : Format(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Renders a count with thousands separators, so a million results reads as one.</summary>
public sealed class CountFormat : IValueConverter
{
    public static readonly CountFormat Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? "0" : System.Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString("N0", CultureInfo.InvariantCulture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound value equals the parameter: an enum member, a string, a number.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value, parameter) ||
        (value is not null && parameter is string text && string.Equals(value.ToString(), text, StringComparison.Ordinal));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
