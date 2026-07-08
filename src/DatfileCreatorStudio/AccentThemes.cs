using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DatfileCreatorStudio;

/// <summary>
/// Named accent themes. Each recolours the Fluent accent (the Start button,
/// radios, checkboxes, sliders, progress bars, selection highlights, focus
/// rings) plus a slim signature bar across the top of the window — enough to
/// make the app stand out against other dark-mode apps, without repainting
/// every surface. "Rainbow" additionally gives the top bar a full spectrum.
/// </summary>
public static class AccentThemes
{
    public const string SystemName = "System";

    /// <summary>One accent theme: a base colour, and whether the top bar is a rainbow.</summary>
    public sealed record AccentDef(string Name, Color Base, bool Rainbow = false);

    public static IReadOnlyList<AccentDef> All { get; } =
    [
        new(SystemName, Color.Parse("#3B82F6")),   // Base is only a fallback; system accent is restored
        new("Azure", Color.Parse("#2E8BEF")),
        new("Emerald", Color.Parse("#14A66E")),
        new("Amethyst", Color.Parse("#8B5CF6")),
        new("Crimson", Color.Parse("#E23B4E")),
        new("Amber", Color.Parse("#E0891C")),
        new("Rose", Color.Parse("#E24E97")),
        new("Cyan", Color.Parse("#0FA3B1")),
        new("Slate", Color.Parse("#6C7A93")),
        new("Rainbow", Color.Parse("#C64DE0"), Rainbow: true),
    ];

    public static string[] Names { get; } = [.. All.Select(a => a.Name)];

    // The accent colour keys the Fluent theme reads.
    private static readonly string[] AccentKeys =
    [
        "SystemAccentColor",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
    ];

    private static Dictionary<string, object> _systemDefaults = [];

    /// <summary>Snapshot the OS accent colours once, so "System" can be restored later.</summary>
    public static void CaptureSystemDefaults()
    {
        if (Application.Current is not { } app)
            return;
        _systemDefaults = [];
        foreach (string key in AccentKeys)
            if (app.TryGetResource(key, null, out object? v) && v is not null)
                _systemDefaults[key] = v;
    }

    /// <summary>Apply an accent theme by name. Must be called on the UI thread.</summary>
    public static void Apply(string? name)
    {
        if (Application.Current is not { } app)
            return;
        var res = app.Resources;
        name ??= SystemName;

        if (name == SystemName)
        {
            foreach (var (key, value) in _systemDefaults)
                res[key] = value;
            Color accent = _systemDefaults.TryGetValue("SystemAccentColor", out object? c) && c is Color cc
                ? cc : Color.Parse("#3B82F6");
            res["AppAccentBarBrush"] = new ImmutableSolidColorBrush(accent);
            return;
        }

        var def = All.FirstOrDefault(a => a.Name == name);
        if (def is null)
        {
            Apply(SystemName);
            return;
        }

        Color b = def.Base;
        res["SystemAccentColor"] = b;
        res["SystemAccentColorLight1"] = Mix(b, Colors.White, 0.15);
        res["SystemAccentColorLight2"] = Mix(b, Colors.White, 0.30);
        res["SystemAccentColorLight3"] = Mix(b, Colors.White, 0.45);
        res["SystemAccentColorDark1"] = Mix(b, Colors.Black, 0.12);
        res["SystemAccentColorDark2"] = Mix(b, Colors.Black, 0.24);
        res["SystemAccentColorDark3"] = Mix(b, Colors.Black, 0.36);
        res["AppAccentBarBrush"] = def.Rainbow ? BuildRainbow() : new ImmutableSolidColorBrush(b);
    }

    private static Color Mix(Color from, Color to, double f)
    {
        byte Ch(byte a, byte c) => (byte)Math.Clamp(a + (c - a) * f, 0, 255);
        return Color.FromArgb(255, Ch(from.R, to.R), Ch(from.G, to.G), Ch(from.B, to.B));
    }

    private static IBrush BuildRainbow() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#FF4D4D"), 0.00),
            new GradientStop(Color.Parse("#FF9A3D"), 0.20),
            new GradientStop(Color.Parse("#FFD93D"), 0.40),
            new GradientStop(Color.Parse("#4DD97A"), 0.60),
            new GradientStop(Color.Parse("#4D9AFF"), 0.80),
            new GradientStop(Color.Parse("#B54DFF"), 1.00),
        },
    };
}
