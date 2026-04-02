using System.Windows.Media;

namespace KeystrokeApp.Services;

/// <summary>
/// All color values needed to paint the suggestion panel for a given theme.
/// </summary>
public record PanelTheme(
    string Id,
    string DisplayName,
    Color ShadowColor,      // DropShadowEffect halo
    Color NormalBorder,     // Resting border (alpha baked in)
    Color SweepPeak,        // Bright center of the loading sweep gradient
    Color SweepSoft,        // Soft falloff shoulders of the sweep gradient
    Color StreamingFlash    // Border flash color when a suggestion arrives
);

public static class ThemeDefinitions
{
    public static readonly PanelTheme Midnight = new(
        "midnight", "Midnight",
        ShadowColor:    Color.FromRgb (0x50, 0x50, 0xE8),
        NormalBorder:   Color.FromArgb(0x30, 0x60, 0x80, 0xFF),
        SweepPeak:      Color.FromArgb(0xCC, 0xA0, 0xA8, 0xFF),
        SweepSoft:      Color.FromArgb(0x30, 0x80, 0x90, 0xFF),
        StreamingFlash: Color.FromArgb(0xFF, 0xA0, 0xA8, 0xFF)
    );

    public static readonly PanelTheme Ember = new(
        "ember", "Ember",
        ShadowColor:    Color.FromRgb (0xD0, 0x60, 0x10),
        NormalBorder:   Color.FromArgb(0x30, 0xFF, 0x90, 0x40),
        SweepPeak:      Color.FromArgb(0xCC, 0xFF, 0xC0, 0x70),
        SweepSoft:      Color.FromArgb(0x30, 0xFF, 0x90, 0x40),
        StreamingFlash: Color.FromArgb(0xFF, 0xFF, 0xC0, 0x70)
    );

    public static readonly PanelTheme Forest = new(
        "forest", "Forest",
        ShadowColor:    Color.FromRgb (0x20, 0x9A, 0x50),
        NormalBorder:   Color.FromArgb(0x30, 0x40, 0xC0, 0x70),
        SweepPeak:      Color.FromArgb(0xCC, 0x80, 0xFF, 0xA0),
        SweepSoft:      Color.FromArgb(0x30, 0x40, 0xC0, 0x60),
        StreamingFlash: Color.FromArgb(0xFF, 0x80, 0xFF, 0xA0)
    );

    public static readonly PanelTheme Rose = new(
        "rose", "Rose",
        ShadowColor:    Color.FromRgb (0xC8, 0x38, 0x98),
        NormalBorder:   Color.FromArgb(0x30, 0xFF, 0x70, 0xD0),
        SweepPeak:      Color.FromArgb(0xCC, 0xFF, 0xA0, 0xE0),
        SweepSoft:      Color.FromArgb(0x30, 0xFF, 0x70, 0xC0),
        StreamingFlash: Color.FromArgb(0xFF, 0xFF, 0xA0, 0xE0)
    );

    public static readonly PanelTheme Slate = new(
        "slate", "Slate",
        ShadowColor:    Color.FromRgb (0x58, 0x68, 0x78),
        NormalBorder:   Color.FromArgb(0x30, 0xA0, 0xB0, 0xC0),
        SweepPeak:      Color.FromArgb(0xCC, 0xC0, 0xD0, 0xE0),
        SweepSoft:      Color.FromArgb(0x30, 0x90, 0xA0, 0xB8),
        StreamingFlash: Color.FromArgb(0xFF, 0xC0, 0xD0, 0xE0)
    );

    private static readonly IReadOnlyDictionary<string, PanelTheme> _all =
        new Dictionary<string, PanelTheme>
        {
            [Midnight.Id] = Midnight,
            [Ember.Id]    = Ember,
            [Forest.Id]   = Forest,
            [Rose.Id]     = Rose,
            [Slate.Id]    = Slate,
        };

    public static PanelTheme Get(string id) =>
        _all.TryGetValue(id, out var t) ? t : Midnight;

    public static IEnumerable<PanelTheme> All => _all.Values;
}
