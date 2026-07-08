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

    /// <summary>All dat generation options (the suite's Settings dataclass equivalent).</summary>
    public DatSettings Dat { get; set; } = new();
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
            var clone = new AppConfig { Theme = Config.Theme, Dat = Config.Dat.Clone() };
            clone.Dat.Date = ""; // never persist the run date
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(clone, JsonOptions));
        }
        catch
        {
            // Non-fatal: settings just won't persist this run (e.g. read-only location)
        }
    }
}
