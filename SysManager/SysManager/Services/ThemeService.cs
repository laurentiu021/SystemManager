// SysManager · ThemeService — runtime theme switching with persistence
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;

namespace SysManager.Services;

public sealed class ThemeService
{
    private static ThemeService? _instance;
    public static ThemeService Instance => _instance ??= new ThemeService();

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SysManager", "theme.json");

    public event Action? ThemeChanged;

    public ThemePreset CurrentTheme { get; private set; } = ThemePreset.Defaults["midnight-indigo"];
    public string CurrentPresetId { get; private set; } = "midnight-indigo";
    public string CurrentMode { get; private set; } = "dark";
    public double ShadePosition { get; private set; } = 0.5;

    private ThemePreset _baseTheme = ThemePreset.Defaults["midnight-indigo"];

    // The shade slider raises SetShade on every tick of a drag; persisting on each one would
    // hammer the disk. Coalesce writes: SetShade applies the shade live but (re)starts this
    // short timer, so the JSON is written once the drag settles. Created lazily on the first
    // shade change (always on the UI thread). Discrete theme changes (SetPreset/SetAccent/
    // SetCustom) still save immediately.
    private DispatcherTimer? _shadeSaveTimer;

    private static readonly Dictionary<string, string> DarkToLight = new()
    {
        ["midnight-indigo"] = "clean-indigo",
        ["deep-ocean"] = "sky-breeze",
        ["dark-forest"] = "mint-fresh",
        ["neon-rose"] = "soft-blossom",
        ["violet-night"] = "lavender",
        ["warm-ember"] = "warm-sand",
    };

    private static readonly Dictionary<string, string> LightToDark =
        DarkToLight.ToDictionary(kv => kv.Value, kv => kv.Key);

    private ThemeService() { }

    public void Initialize()
    {
        Load();
        Apply(CurrentTheme);
    }

    public string GetCompanionPreset(string targetMode)
    {
        if (targetMode == "dark" && LightToDark.TryGetValue(CurrentPresetId, out var darkId))
            return darkId;
        if (targetMode == "light" && DarkToLight.TryGetValue(CurrentPresetId, out var lightId))
            return lightId;
        return targetMode == "dark" ? "midnight-indigo" : "clean-indigo";
    }

    public void SetPreset(string id)
    {
        if (!ThemePreset.Defaults.TryGetValue(id, out var preset)) return;
        CurrentPresetId = id;
        _baseTheme = preset;
        CurrentMode = preset.IsDark ? "dark" : "light";
        ApplyShade();
        Save();
    }

    public void SetAccent(Color accent)
    {
        _baseTheme = _baseTheme with { Accent = accent };
        ApplyShade();
        Save();
    }

    public void SetShade(double position)
    {
        ShadePosition = Math.Clamp(position, 0, 1);
        ApplyShade();
        DebouncedSave();
    }

    // Coalesces rapid shade-slider writes into a single disk save once the drag settles.
    private void DebouncedSave()
    {
        _shadeSaveTimer ??= CreateShadeSaveTimer();
        _shadeSaveTimer.Stop();
        _shadeSaveTimer.Start();
    }

    private DispatcherTimer CreateShadeSaveTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => { timer.Stop(); Save(); };
        return timer;
    }

    public void SetCustom(Color accent, Color background, Color surface, Color text)
    {
        CurrentMode = "custom";
        CurrentPresetId = "custom";
        _baseTheme = new ThemePreset(
            Id: "custom",
            Name: "Custom",
            IsDark: background.R + background.G + background.B < 384,
            Accent: accent,
            Background: background,
            Surface: surface,
            Surface2: Lerp(surface, background, 0.5),
            Border: Lerp(surface, text, 0.15),
            TextPrimary: text,
            TextSecondary: Lerp(text, background, 0.3),
            TextMuted: Lerp(text, background, 0.55));
        CurrentTheme = new ThemePreset(
            Id: "custom",
            Name: "Custom",
            IsDark: background.R + background.G + background.B < 384,
            Accent: accent,
            Background: background,
            Surface: surface,
            Surface2: Lerp(surface, background, 0.5),
            Border: Lerp(surface, text, 0.15),
            TextPrimary: text,
            TextSecondary: Lerp(text, background, 0.3),
            TextMuted: Lerp(text, background, 0.55));
        Apply(CurrentTheme);
        Save();
    }

    private void ApplyShade()
    {
        var t = _baseTheme;
        var offset = (ShadePosition - 0.5) * 0.12;

        CurrentTheme = t with
        {
            Background = ShiftLightness(t.Background, offset),
            Surface = ShiftLightness(t.Surface, offset),
            Surface2 = ShiftLightness(t.Surface2, offset),
            Border = ShiftLightness(t.Border, offset)
        };
        Apply(CurrentTheme);
    }

    public void Apply(ThemePreset theme)
    {
        var res = Application.Current.Resources;
        SetBrush(res, "Surface0", theme.Background);
        SetBrush(res, "Surface1", theme.Surface);
        SetBrush(res, "Surface2", theme.Surface2);
        SetBrush(res, "Surface3", Lerp(theme.Surface2, theme.TextPrimary, 0.05));
        SetBrush(res, "Surface4", Lerp(theme.Surface2, theme.TextPrimary, 0.1));
        SetBrush(res, "Border1", theme.Border);
        SetBrush(res, "Border2", Lerp(theme.Border, theme.TextPrimary, 0.08));
        SetBrush(res, "BorderAccent", Lerp(theme.Border, theme.Accent, 0.2));
        SetBrush(res, "TextPrimary", theme.TextPrimary);
        SetBrush(res, "TextSecondary", theme.TextSecondary);
        SetBrush(res, "TextMuted", theme.TextMuted);
        SetBrush(res, "TextDisabled", Lerp(theme.TextMuted, theme.Background, 0.4));
        SetBrush(res, "Accent", theme.Accent);
        SetBrush(res, "AccentHover", Lighten(theme.Accent, 0.15));
        SetBrush(res, "AccentPressed", Darken(theme.Accent, 0.12));
        SetBrush(res, "AccentSoft", Color.FromArgb(24, theme.Accent.R, theme.Accent.G, theme.Accent.B));

        SetColor(res, "Surface0Color", theme.Background);
        SetColor(res, "Surface1Color", theme.Surface);
        SetColor(res, "Surface2Color", theme.Surface2);
        SetColor(res, "Surface3Color", Lerp(theme.Surface2, theme.TextPrimary, 0.05));
        SetColor(res, "Surface4Color", Lerp(theme.Surface2, theme.TextPrimary, 0.1));
        SetColor(res, "AccentColor", theme.Accent);
        SetColor(res, "AccentHoverColor", Lighten(theme.Accent, 0.15));
        SetColor(res, "AccentPressedColor", Darken(theme.Accent, 0.12));

        // Card depth (the revamp's signature "glass depth"): a subtle top sheen + a top-lit rim.
        // Both are theme-DERIVED so they survive all 12 presets + custom + shade, and both are pure
        // gradient fills (no DropShadowEffect) so they stay PERF-008-safe on the many repeated cards.
        //  - Sheen: a vertical gradient whose top is a hair lifted off the surface, fading to the flat
        //    surface by 55% — reads as "lit from above". Lifted on dark, tinted-down on light, matching
        //    the approved mockup's cardgrad (white-alpha on dark / dark-alpha on light).
        //  - Rim: a vertical border gradient, brighter/darker at the very top (Lerp toward TextPrimary)
        //    fading to the normal border by mid-height — the 1px contour highlight without a shadow.
        var sheenTop = theme.IsDark ? Lighten(theme.Surface, 0.05) : Darken(theme.Surface, 0.02);
        SetBrush(res, "CardSurface", VGradient((sheenTop, 0.0), (theme.Surface, 0.55)));
        var rim = Lerp(theme.Border, theme.TextPrimary, 0.22);
        SetBrush(res, "CardRim", VGradient((rim, 0.0), (theme.Border, 0.5)));

        // Row hover — a SUBTLE neutral tint, deliberately distinct from the accent-tinted selection
        // (AccentSoft). Before, DataGrid rows used AccentSoft for BOTH hover and selection, so hovering
        // any row made it look selected. This is a faint lift off the surface (toward TextPrimary),
        // theme-derived so it works on light presets too.
        SetBrush(res, "RowHover", Lerp(theme.Surface, theme.TextPrimary, 0.05));

        ApplyStatusBrushes(res, theme.IsDark);

        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// Builds a frozen top-to-bottom <see cref="LinearGradientBrush"/> from the given color/offset
    /// stops (StartPoint 0.5,0 → EndPoint 0.5,1). Used for the card sheen + rim-light; the last stop's
    /// color holds to the bottom edge. Frozen so it is shareable and cheap across many cards.
    /// </summary>
    private static LinearGradientBrush VGradient(params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0.5, 0), EndPoint = new(0.5, 1) };
        foreach (var (color, offset) in stops)
            brush.GradientStops.Add(new GradientStop(color, offset));
        brush.Freeze();
        return brush;
    }

    private static void SetBrush(ResourceDictionary res, string key, Brush brush)
    {
        if (brush.CanFreeze) brush.Freeze();
        res[key] = brush;
    }

    /// <summary>
    /// Recomputes the semantic status brushes (Warning / Success / Info / Danger) for the current
    /// mode. The App.xaml defaults are calibrated for dark surfaces — pale, high-lightness text
    /// (e.g. WarningText #FCD34D) that reads on a near-black banner but washes out to illegible on a
    /// light preset's near-white surface. On light themes we swap to darker, saturated text colors
    /// that meet WCAG AA on white, and lift the subtle background tints so the banner is still
    /// distinguishable. On dark themes we restore the original palette so nothing changes there.
    /// This is the single seam that fixes every hardcoded-warning-banner contrast defect at once.
    /// </summary>
    private static void ApplyStatusBrushes(ResourceDictionary res, bool isDark)
    {
        foreach (var (key, color) in StatusPalette(isDark))
            SetBrush(res, key, color);
    }

    /// <summary>
    /// Pure color decision for the semantic status brushes, split out so it is unit-testable without
    /// a WPF Application (the actual brush assignment writes into Application.Current.Resources).
    /// Dark values mirror the App.xaml defaults; light values are darker, saturated text that meets
    /// WCAG AA against a near-white surface, with tints lifted so the banner still reads as coloured.
    /// </summary>
    public static IReadOnlyList<(string Key, Color Color)> StatusPalette(bool isDark) => isDark
        ?
        [
            ("WarningText", C("#FCD34D")), ("WarningBgSubtle", C("#1AFBBF24")),
            ("WarningBg", C("#40FBBF24")), ("WarningStripe", C("#FBBF24")),
            ("SuccessText", C("#4ADE80")), ("SuccessBgSubtle", C("#1A22C55E")), ("SuccessBorder", C("#3322C55E")),
            ("InfoText", C("#7DD3FC")), ("InfoBgSubtle", C("#1A38BDF8")), ("InfoBorder", C("#3338BDF8")),
            ("DangerText", C("#F87171")), ("DangerBgSubtle", C("#1AEF4444")), ("DangerBorder", C("#33EF4444")),

            // Base semantic brushes (used directly as small-text Foreground / dot Fill across the app,
            // e.g. Cleanup's TEMP-folders stat). These were static App.xaml resources that never
            // recomputed per mode, so their light-cyan/green/amber/red washed out on near-white light
            // surfaces. Dark values mirror the App.xaml defaults exactly (no visual change on dark).
            ("Info", C("#38BDF8")), ("Success", C("#22C55E")), ("Warning", C("#F59E0B")), ("Danger", C("#EF4444")),

            // Console output palette (ConsoleView). Also static-only before, so light-theme consoles
            // rendered near-invisible pale-grey body text on their near-white card. Dark values mirror
            // the App.xaml defaults (no visual change on dark).
            ("OutOutputBrush", C("#E6E6E6")), ("OutVerboseBrush", C("#9AA0A6")),
            ("OutInfoBrush", C("#38BDF8")), ("OutWarnBrush", C("#FBBF24")), ("OutErrorBrush", C("#F87171")),
            ("OutDebugBrush", C("#B388FF")), ("OutProgressBrush", C("#4ADE80")),
        ]
        :
        [
            ("WarningText", C("#92400E")), ("WarningBgSubtle", C("#26FBBF24")),   // amber-800 text — AA on white
            ("WarningBg", C("#40FBBF24")), ("WarningStripe", C("#D97706")),
            ("SuccessText", C("#15803D")), ("SuccessBgSubtle", C("#2622C55E")), ("SuccessBorder", C("#5522C55E")),
            ("InfoText", C("#0369A1")), ("InfoBgSubtle", C("#2638BDF8")), ("InfoBorder", C("#5538BDF8")),
            ("DangerText", C("#B91C1C")), ("DangerBgSubtle", C("#26EF4444")), ("DangerBorder", C("#55EF4444")),

            // Light: darker, saturated tones that meet WCAG AA as SMALL text — not just on pure white,
            // but on the most-tinted light preset card surface (soft-blossom Surface2 #FBCFE8 is the
            // worst case). Info #075985 / Success #166534 / Warning #9A3412 all clear 4.5:1 there;
            // Danger #B91C1C already did. (Earlier values passed on #FFFFFF but dipped to ~3.6-4.3 on
            // the pastel presets — see ThemeStatusBrushTests, which now asserts against the tinted surface.)
            ("Info", C("#075985")), ("Success", C("#166534")), ("Warning", C("#9A3412")), ("Danger", C("#B91C1C")),

            // Console on light: dark-on-white body/verbose text; semantic lines reuse the AA light tones.
            ("OutOutputBrush", C("#1E1B4B")), ("OutVerboseBrush", C("#475569")),
            ("OutInfoBrush", C("#075985")), ("OutWarnBrush", C("#9A3412")), ("OutErrorBrush", C("#B91C1C")),
            ("OutDebugBrush", C("#6D28D9")), ("OutProgressBrush", C("#166534")),
        ];

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static void SetBrush(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }

    private static void SetColor(ResourceDictionary res, string key, Color color)
    {
        res[key] = color;
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static Color Lighten(Color c, double amount)
    {
        return Color.FromArgb(c.A,
            (byte)Math.Min(255, c.R + (255 - c.R) * amount),
            (byte)Math.Min(255, c.G + (255 - c.G) * amount),
            (byte)Math.Min(255, c.B + (255 - c.B) * amount));
    }

    private static Color Darken(Color c, double amount)
    {
        return Color.FromArgb(c.A,
            (byte)(c.R * (1 - amount)),
            (byte)(c.G * (1 - amount)),
            (byte)(c.B * (1 - amount)));
    }

    private static Color ShiftLightness(Color c, double amount)
    {
        if (amount >= 0)
            return Lighten(c, amount);
        return Darken(c, -amount);
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var data = new ThemeSettings(CurrentPresetId, CurrentMode, ShadePosition,
                CurrentTheme.Accent.ToString(), CurrentTheme.Background.ToString(),
                CurrentTheme.Surface.ToString(), CurrentTheme.TextPrimary.ToString());
            var json = JsonSerializer.Serialize(data, JsonDefaults.Indented);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex) { Log.Debug("Theme save failed: {Error}", ex.Message); }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var data = JsonSerializer.Deserialize<ThemeSettings>(json);
            if (data is null) return;

            CurrentMode = data.Mode;
            ShadePosition = data.ShadePosition;
            CurrentPresetId = data.PresetId;

            if (data.PresetId == "custom")
            {
                var accent = (Color)ColorConverter.ConvertFromString(data.Accent);
                var bg = (Color)ColorConverter.ConvertFromString(data.Background);
                var surface = (Color)ColorConverter.ConvertFromString(data.Surface);
                var text = (Color)ColorConverter.ConvertFromString(data.Text);
                SetCustom(accent, bg, surface, text);
            }
            else if (ThemePreset.Defaults.TryGetValue(data.PresetId, out var preset))
            {
                _baseTheme = preset;
                CurrentTheme = preset;
                ApplyShade();
            }
        }
        catch (Exception ex) { Log.Debug("Theme load failed: {Error}", ex.Message); }
    }

    private sealed record ThemeSettings(
        string PresetId, string Mode, double ShadePosition,
        string Accent, string Background, string Surface, string Text);
}

public sealed record ThemePreset(
    string Id,
    string Name,
    bool IsDark,
    Color Accent,
    Color Background,
    Color Surface,
    Color Surface2,
    Color Border,
    Color TextPrimary,
    Color TextSecondary,
    Color TextMuted)
{
    public static readonly Dictionary<string, ThemePreset> Defaults = new()
    {
        ["midnight-indigo"] = new("midnight-indigo", "Midnight Indigo", true,
            C("#6366F1"), C("#070A0F"), C("#0E1218"), C("#151A23"), C("#1F2633"),
            C("#F1F3F7"), C("#A3ADBF"), C("#7B8396")), // muted: WCAG AA on Surface2 (was #6B7489, 3.72)
        ["deep-ocean"] = new("deep-ocean", "Deep Ocean", true,
            C("#3B82F6"), C("#050D1A"), C("#0A1628"), C("#0F1D33"), C("#1A2D4D"),
            C("#E2E8F0"), C("#94A3B8"), C("#78879B")), // muted: WCAG AA on Surface2 (was #64748B, 3.55)
        ["dark-forest"] = new("dark-forest", "Dark Forest", true,
            C("#10B981"), C("#020F0A"), C("#021A12"), C("#03261A"), C("#0A3D2A"),
            C("#D1FAE5"), C("#6EE7B7"), C("#34D399")),
        ["neon-rose"] = new("neon-rose", "Neon Rose", true,
            C("#EC4899"), C("#120508"), C("#1A0A0F"), C("#240E16"), C("#3D1525"),
            C("#FDF2F8"), C("#F9A8D4"), C("#F472B6")),
        ["violet-night"] = new("violet-night", "Violet Night", true,
            C("#A855F7"), C("#0A0515"), C("#0F0A1A"), C("#160F26"), C("#2D1B4E"),
            C("#F3E8FF"), C("#C4B5FD"), C("#8E60F6")), // muted: WCAG AA on Surface2 (was #8B5CF6, 4.39)
        ["warm-ember"] = new("warm-ember", "Warm Ember", true,
            C("#F59E0B"), C("#0F0A04"), C("#1A1008"), C("#24180C"), C("#3D2A12"),
            C("#FEF3C7"), C("#FCD34D"), C("#FBBF24")),
        ["clean-indigo"] = new("clean-indigo", "Clean Indigo", false,
            C("#6366F1"), C("#FFFFFF"), C("#F8FAFC"), C("#F1F5F9"), C("#E2E8F0"),
            C("#1E1B4B"), C("#4338CA"), C("#5D5FE2")), // muted: WCAG AA on Surface2 (was #6366F1, 4.08)
        ["sky-breeze"] = new("sky-breeze", "Sky Breeze", false,
            C("#0EA5E9"), C("#F8FAFC"), C("#F0F9FF"), C("#E0F2FE"), C("#BAE6FD"),
            C("#0C4A6E"), C("#0369A1"), C("#0572AB")), // muted: WCAG AA on Surface2 (was #0284C7, 3.57)
        ["warm-sand"] = new("warm-sand", "Warm Sand", false,
            C("#D97706"), C("#FFFBEB"), C("#FEF3C7"), C("#FDE68A"), C("#FCD34D"),
            C("#451A03"), C("#78350F"), C("#92400E")),
        ["mint-fresh"] = new("mint-fresh", "Mint Fresh", false,
            C("#16A34A"), C("#F0FDF4"), C("#DCFCE7"), C("#BBF7D0"), C("#86EFAC"),
            C("#14532D"), C("#166534"), C("#15783A")), // muted: WCAG AA on Surface2 (was #15803D, 4.14)
        ["soft-blossom"] = new("soft-blossom", "Soft Blossom", false,
            C("#DB2777"), C("#FDF2F8"), C("#FCE7F3"), C("#FBCFE8"), C("#F9A8D4"),
            C("#500724"), C("#831843"), C("#9D174D")),
        ["lavender"] = new("lavender", "Lavender", false,
            C("#7C3AED"), C("#FAF5FF"), C("#F3E8FF"), C("#E9D5FF"), C("#D8B4FE"),
            C("#2E1065"), C("#4C1D95"), C("#5B21B6")),
    };

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
