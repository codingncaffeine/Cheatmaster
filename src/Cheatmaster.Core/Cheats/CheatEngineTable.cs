using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.Core.Cheats;

/// <summary>One entry that could not be brought across, and why.</summary>
public sealed record ImportProblem(string Description, string Reason);

/// <summary>What an import produced, including everything it refused to guess at.</summary>
public sealed class ImportReport
{
    public List<CheatEntry> Entries { get; } = [];

    public List<ImportProblem> Skipped { get; } = [];

    /// <summary>Entries that came across but need the game running to mean anything.</summary>
    public int SessionOnly { get; set; }

    public string? Error { get; set; }

    public bool Failed => Error is not null;

    public string Summary
    {
        get
        {
            if (Error is not null) return Error;

            var text = new StringBuilder();
            text.Append(Entries.Count == 1 ? "1 entry imported" : $"{Entries.Count} entries imported");
            if (Skipped.Count > 0) text.Append($", {Skipped.Count} could not be");
            if (SessionOnly > 0) text.Append($" · {SessionOnly} anchored to a raw address, so only good while the game runs");
            return text.Append('.').ToString();
        }
    }
}

/// <summary>
/// Reads a Cheat Engine table.
///
/// The data model lines up almost exactly — an address, a type, a chain of offsets — so most of a
/// table comes across unchanged. What does not come across is the half of Cheat Engine that is a
/// programming environment: auto assembler scripts and Lua. Those are reported entry by entry
/// rather than dropped, because a table that looks imported and silently does nothing is worse
/// than one that refuses.
///
/// ⚠ The offsets are listed in the order Cheat Engine shows them, which is the reverse of the
/// order they are applied. Get that backwards and every imported pointer entry reads a plausible
/// wrong address while looking perfectly correct.
/// </summary>
public static class CheatEngineTable
{
    public static ImportReport Load(string path)
    {
        try
        {
            return Parse(ReadText(path));
        }
        catch (Exception ex)
        {
            return new ImportReport { Error = "That file could not be read as a Cheat Engine table: " + ex.Message };
        }
    }

    /// <summary>A table saved as compressed is a zip holding the same XML under another name.</summary>
    private static string ReadText(string path)
    {
        using var file = File.OpenRead(path);

        Span<byte> header = stackalloc byte[2];
        bool zipped = file.Read(header) == 2 && header[0] == 'P' && header[1] == 'K';
        file.Position = 0;

        if (!zipped) return new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true).ReadToEnd();

        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault()
                    ?? throw new InvalidDataException("the archive is empty");
        using var stream = entry.Open();
        return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true).ReadToEnd();
    }

    public static ImportReport Parse(string xml)
    {
        var report = new ImportReport();

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            report.Error = "That file is not a readable cheat table: " + ex.Message;
            return report;
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "CheatTable")
        {
            report.Error = "That file is not a Cheat Engine table.";
            return report;
        }

        ReadEntries(root.Element("CheatEntries"), string.Empty, report);
        if (report.Entries.Count == 0 && report.Skipped.Count == 0)
            report.Error = "That table has no entries in it.";

        return report;
    }

    /// <summary>
    /// Cheat Engine nests entries under a header to group them. That is the same idea as a named
    /// cheat here, so the header's name becomes the group and the tree flattens.
    /// </summary>
    private static void ReadEntries(XElement? container, string group, ImportReport report)
    {
        if (container is null) return;

        foreach (var element in container.Elements("CheatEntry"))
        {
            string description = Unquote(Text(element, "Description")) is { Length: > 0 } d ? d : "Imported entry";
            var children = element.Element("CheatEntries");

            // A header names everything underneath it. It usually holds no address of its own,
            // and when it does, Cheat Engine keeps both.
            if (children is not null) ReadEntries(children, description, report);

            bool isHeader = Text(element, "GroupHeader") == "1";
            if (isHeader && Text(element, "Address").Length == 0) continue;

            if (element.Element("AssemblerScript") is not null)
            {
                report.Skipped.Add(new ImportProblem(description,
                    "It is an auto assembler script, which changes the game's code rather than a value."));
                continue;
            }

            if (element.Element("LuaScript") is not null)
            {
                report.Skipped.Add(new ImportProblem(description, "It is a Lua script."));
                continue;
            }

            string typeName = Text(element, "VariableType");
            if (!TryMapType(typeName, out ScanType type))
            {
                report.Skipped.Add(new ImportProblem(description,
                    typeName.Length == 0 ? "It has no value type." : $"'{typeName}' is not a kind of value this can hold."));
                continue;
            }

            if (Text(element, "ShowAsSigned") == "0") type = Unsigned(type);

            string addressText = Text(element, "Address");
            if (!TryParseAddress(addressText, out AddressSpec address, out string addressProblem))
            {
                report.Skipped.Add(new ImportProblem(description, addressProblem));
                continue;
            }

            if (!TryReadOffsets(element, out int[] pointers, out string offsetProblem))
            {
                report.Skipped.Add(new ImportProblem(description, offsetProblem));
                continue;
            }

            address.Pointers = pointers;

            var entry = new CheatEntry
            {
                Description = description,
                Address = address,
                Type = type,
                Group = group
            };

            // A table can arrive with a value the author was holding. It is kept, but nothing is
            // frozen on import: importing a table must not start writing into a game by itself.
            var lastState = element.Element("LastState");
            if (lastState?.Attribute("Activated")?.Value == "1")
                entry.FreezeValue = lastState.Attribute("Value")?.Value ?? string.Empty;

            if (address.IsSessionOnly) report.SessionOnly++;
            report.Entries.Add(entry);
        }
    }

    /// <summary>
    /// Turns the listed offsets into the order they are applied.
    ///
    /// Cheat Engine resolves a chain from the last offset in the file backwards to the first
    /// (<c>for i := offsetCount-1 downto 0</c> in GetRealAddress) while writing them out in array
    /// order, so the first offset in the file is the last one applied. Ours are applied in the
    /// order they are stored, which is the reverse. Every offset is written as hex, and may be a
    /// symbolic expression instead of a number — which cannot be honoured here, and must not be
    /// quietly dropped, because a chain missing a step still resolves to a plausible wrong place.
    /// </summary>
    private static bool TryReadOffsets(XElement entry, out int[] pointers, out string problem)
    {
        pointers = [];
        problem = string.Empty;

        var offsets = entry.Element("Offsets");
        if (offsets is null) return true;

        var listed = new List<int>();
        foreach (var offset in offsets.Elements("Offset"))
        {
            if (!TryParseSignedHex(offset.Value, out int value))
            {
                problem = $"One of its pointer offsets is '{offset.Value.Trim()}', which is worked out while the "
                          + "game runs rather than being a fixed number.";
                return false;
            }
            listed.Add(value);
        }

        listed.Reverse();
        pointers = [.. listed];
        return true;
    }

    private static bool TryMapType(string name, out ScanType type)
    {
        type = ScanType.Int32;
        switch (name.Trim().ToLowerInvariant())
        {
            case "byte": type = ScanType.Int8; return true;
            case "2 bytes": type = ScanType.Int16; return true;
            case "4 bytes": type = ScanType.Int32; return true;
            case "8 bytes": type = ScanType.Int64; return true;
            case "float": type = ScanType.Float; return true;
            case "double": type = ScanType.Double; return true;
            default: return false;
        }
    }

    private static ScanType Unsigned(ScanType type) => type switch
    {
        ScanType.Int8 => ScanType.UInt8,
        ScanType.Int16 => ScanType.UInt16,
        ScanType.Int32 => ScanType.UInt32,
        ScanType.Int64 => ScanType.UInt64,
        _ => type
    };

    /// <summary>
    /// Reads the three shapes a Cheat Engine address comes in: a module and an offset, a bare
    /// address, and a thread stack, which has no equivalent here and is refused rather than
    /// imported broken.
    /// </summary>
    internal static bool TryParseAddress(string text, out AddressSpec address, out string problem)
    {
        address = new AddressSpec();
        problem = string.Empty;

        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            problem = "It has no address.";
            return false;
        }

        if (trimmed.StartsWith("THREADSTACK", StringComparison.OrdinalIgnoreCase))
        {
            problem = "It is anchored to a thread's stack, which this cannot follow.";
            return false;
        }

        int split = SplitPoint(trimmed);
        if (split < 0)
        {
            if (TryParseHex(trimmed, out ulong absolute))
            {
                address = AddressSpec.Absolute(absolute);
                return true;
            }

            // A bare name is the module itself, at offset zero.
            string bare = Unquote(trimmed);
            if (LooksLikeModule(bare))
            {
                address = AddressSpec.Relative(bare, 0);
                return true;
            }

            problem = $"'{trimmed}' is not an address this understands.";
            return false;
        }

        string module = Unquote(trimmed[..split].Trim());
        string offsetText = trimmed[(split + 1)..].Trim();
        bool subtract = trimmed[split] == '-';

        if (!LooksLikeModule(module) || !TryParseHex(offsetText, out ulong offset))
        {
            problem = $"'{trimmed}' is not an address this understands.";
            return false;
        }

        // A negative module offset would wrap below the module base, which no entry here can mean.
        if (subtract)
        {
            problem = $"'{trimmed}' points before the start of {module}, which this cannot express.";
            return false;
        }

        address = AddressSpec.Relative(module, offset);
        return true;
    }

    /// <summary>The +/- that separates a module from its offset, ignoring any inside quotes.</summary>
    private static int SplitPoint(string text)
    {
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') quoted = !quoted;
            else if (!quoted && (c == '+' || c == '-')) return i;
        }
        return -1;
    }

    private static bool LooksLikeModule(string name) =>
        name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool TryParseHex(string text, out ulong value)
    {
        string cleaned = text.Trim();
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[2..];
        return ulong.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseSignedHex(string text, out int value)
    {
        value = 0;
        string cleaned = text.Trim();
        bool negative = cleaned.StartsWith('-');
        if (negative || cleaned.StartsWith('+')) cleaned = cleaned[1..];

        if (!TryParseHex(cleaned, out ulong magnitude) || magnitude > int.MaxValue) return false;

        value = negative ? -(int)magnitude : (int)magnitude;
        return true;
    }

    /// <summary>Descriptions are written with their quotes in the file.</summary>
    private static string Unquote(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"') return trimmed[1..^1];
        return trimmed;
    }

    private static string Text(XElement element, string name) => element.Element(name)?.Value.Trim() ?? string.Empty;
}
