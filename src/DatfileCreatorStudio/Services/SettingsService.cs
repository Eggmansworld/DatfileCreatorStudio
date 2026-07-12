using System.Text.Json;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.Services;

/// <summary>Everything persisted by the application, stored as a single portable file.</summary>
public sealed class AppConfig
{
    /// <summary>Theme selection: "System", "Light", or "Dark"</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Accent theme name (see AccentThemes); "System" uses the OS accent colour.</summary>
    public string AccentTheme { get; set; } = "System";

    /// <summary>Copper-bars easter-egg settings (active only with the Rainbow accent).</summary>
    public CopperSettings Copper { get; set; } = new();

    /// <summary>End-of-run audio cue settings.</summary>
    public SoundSettings Sound { get; set; } = new();

    /// <summary>All dat generation options (the suite's Settings dataclass equivalent).</summary>
    public DatSettings Dat { get; set; } = new();
}

/// <summary>Settings for the audio cue played when a datfile run finishes.</summary>
public sealed class SoundSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path to a .wav or .mp3; empty = the bundled default in the sounds
    /// folder. Relative paths resolve against the folder holding the exe.
    /// </summary>
    public string FilePath { get; set; } = "";

    public SoundSettings Clone() => (SoundSettings)MemberwiseClone();
}

/// <summary>
/// Tunables for the Rainbow-accent copper-bar background animation. Defaults
/// are deliberately gentle so the effect is a subtle surprise, not a strobe.
/// </summary>
public sealed class CopperSettings
{
    /// <summary>Vertical drift rate, 0 (still) .. 1 (brisk).</summary>
    public double Speed { get; set; } = 0.35;

    /// <summary>Bar thickness in pixels.</summary>
    public double BarSize { get; set; } = 28;

    /// <summary>Colour-cycling rate, 0 (frozen hue) .. 1 (fast).</summary>
    public double CycleSpeed { get; set; } = 0.25;

    /// <summary>Organic sway amount, 0 .. 1.</summary>
    public double Wiggle { get; set; } = 0.35;

    public CopperSettings Clone() => (CopperSettings)MemberwiseClone();
}

/// <summary>
/// Loads and saves the single DatfileCreatorStudio.config file kept next to
/// the executable, so the application is fully portable: no registry, no user
/// profile folders, no system temp files.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppConfig Config { get; private set; } = new();

    /// <summary>The single portable config file, next to DatfileCreatorStudio.exe</summary>
    public static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "DatfileCreatorStudio.config");

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch
        {
            // A corrupt config should never prevent startup; fall back to defaults
            Config = new AppConfig();
        }
        // The run date is runtime-only, exactly like the Python suite
        Config.Dat.Date = "";
        // Same clamp the suite applies when loading
        Config.Dat.Threads = Math.Clamp(Config.Dat.Threads, 1, 8);
    }

    public void Save()
    {
        try
        {
            var clone = new AppConfig
            {
                Theme = Config.Theme,
                AccentTheme = Config.AccentTheme,
                Copper = Config.Copper.Clone(),
                Sound = Config.Sound.Clone(),
                Dat = Config.Dat.Clone(),
            };
            clone.Dat.Date = ""; // never persist the run date
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(clone, JsonOptions));
        }
        catch
        {
            // Non-fatal: settings just won't persist this run (e.g. read-only location)
        }
    }
}
