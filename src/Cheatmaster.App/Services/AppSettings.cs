using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.Services;

/// <summary>Everything the app remembers between runs.</summary>
public sealed class AppSettings
{
    public int Alignment { get; set; } = 4;
    public ScanProfile Profile { get; set; } = ScanProfile.Standard;
    public RoundingMode Rounding { get; set; } = RoundingMode.Display;

    public bool WritableOnly { get; set; } = true;
    public bool IncludeImage { get; set; } = true;
    public bool IncludePrivate { get; set; } = true;
    public bool IncludeMapped { get; set; } = true;
    public bool SkipExecutable { get; set; }

    public bool AutoSaveTables { get; set; } = true;

    /// <summary>Look up cover art and a description for saved games from the public store listing.</summary>
    public bool FetchArtwork { get; set; } = true;

    /// <summary>Back the library up to GitHub when the app starts, once signed in.</summary>
    public bool AutoBackup { get; set; }
    public DateTimeOffset? LastSyncUtc { get; set; }
    public bool AutoLoadTables { get; set; } = true;

    /// <summary>The first-run walkthrough opens by itself until it has been finished or skipped once.</summary>
    public bool GuideDismissed { get; set; }
    public bool LiveValues { get; set; } = true;
    public int MaxResultsPerInterpretation { get; set; } = 2_000_000;

    public double WindowWidth { get; set; } = 1420;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cheatmaster");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch
        {
            // A damaged settings file should never stop the app starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Losing preferences is not worth an error dialog.
        }
    }

    public void ApplyTo(ScanSettings scan)
    {
        scan.Alignment = Alignment;
        scan.MaxResultsPerInterpretation = MaxResultsPerInterpretation;
        scan.Regions.WritableOnly = WritableOnly;
        scan.Regions.IncludeImage = IncludeImage;
        scan.Regions.IncludePrivate = IncludePrivate;
        scan.Regions.IncludeMapped = IncludeMapped;
        scan.Regions.SkipExecutable = SkipExecutable;
    }
}
