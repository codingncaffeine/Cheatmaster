using System.Text.Json.Serialization;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.Core.Cheats;

/// <summary>One saved value in a cheat table.</summary>
public sealed class CheatEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Description { get; set; } = "New entry";

    public AddressSpec Address { get; set; } = new();

    public ScanType Type { get; set; } = ScanType.Int32;

    public int ScaleNum { get; set; } = 1;
    public int ScaleDen { get; set; } = 1;
    public bool BigEndian { get; set; }
    public ulong XorKey { get; set; }
    public long Bias { get; set; }

    /// <summary>Keep writing <see cref="FreezeValue"/> over whatever the game puts there.</summary>
    public bool Frozen { get; set; }

    public string FreezeValue { get; set; } = string.Empty;

    /// <summary>Global hotkey that toggles the freeze, e.g. "Ctrl+F1". Empty for none.</summary>
    public string Hotkey { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    [JsonIgnore]
    public Interpretation Interpretation =>
        new(Type, ScaleNum, ScaleDen, BigEndian, XorKey, Bias);

    public void SetInterpretation(in Interpretation interpretation)
    {
        Type = interpretation.Type;
        ScaleNum = interpretation.ScaleNum;
        ScaleDen = interpretation.ScaleDen;
        BigEndian = interpretation.BigEndian;
        XorKey = interpretation.XorKey;
        Bias = interpretation.Bias;
    }

    public bool TryReadValue(TargetProcess process, out ulong bits)
    {
        bits = 0;
        ulong address = Address.Resolve(process);
        if (address == 0) return false;

        Span<byte> buffer = stackalloc byte[8];
        int width = Type.Width();
        if (!process.ReadExact(address, buffer[..width])) return false;
        bits = Raw.ReadBits(Type, buffer);
        return true;
    }

    public bool TryWriteValue(TargetProcess process, ulong bits)
    {
        ulong address = Address.Resolve(process);
        if (address == 0) return false;

        Span<byte> buffer = stackalloc byte[8];
        int width = Type.Width();
        Raw.WriteBytes(Type, bits, buffer);
        return process.Write(address, buffer[..width]);
    }

    public bool TryWriteDisplayValue(TargetProcess process, string text)
    {
        var value = UserValue.Parse(text);
        if (!value.IsValid) return false;

        var interpretation = Interpretation;
        double display = value.FitsDecimal ? (double)value.Dec : value.Dbl;
        return interpretation.TryEncodeExact(display, out ulong bits) && TryWriteValue(process, bits);
    }

    public CheatEntry Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Description = Description,
        Address = Address.Clone(),
        Type = Type,
        ScaleNum = ScaleNum,
        ScaleDen = ScaleDen,
        BigEndian = BigEndian,
        XorKey = XorKey,
        Bias = Bias,
        Frozen = false,
        FreezeValue = FreezeValue,
        Hotkey = string.Empty,
        Group = Group,
        Notes = Notes
    };
}
