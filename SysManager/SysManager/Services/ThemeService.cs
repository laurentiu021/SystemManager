// SysManager · ThemeService — runtime theme switching with persistence
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using SysManager.Helpers;

namespace SysManager.Services;

public sealed class ThemeService
{
    private static ThemeService? _instance;
    public static ThemeService Instance => _instance ??= new ThemeService();

    private readonly string _settingsPath;

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

    /// <summary>
    /// Production goes through <see cref="Instance"/>, which passes null and lands on the real
    /// <c>%AppData%\SysManager\theme.json</c> (ROAMING, where this file has always lived — changing it
    /// would strand a user's saved theme). The <paramref name="configDir"/> seam exists so a test can
    /// point the service at a temp directory instead of the developer's real theme; without it, every
    /// test that constructed the service and saved would overwrite that file, the same class of data
    /// loss that hit SpeedTestHistoryService (#1734, #1741). Internal because nothing outside the
    /// assembly should build a second theme service — the app has exactly one, via <see cref="Instance"/>.
    /// </summary>
    internal ThemeService(string? configDir = null)
    {
        _settingsPath = Path.Combine(
            configDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysManager"),
            "theme.json");
    }

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
        CurrentTheme = Shade(_baseTheme, ShadePosition);
        Apply(CurrentTheme);
    }

    /// <summary>
    /// Applies the background-shade position to a preset: shifts the surfaces, then keeps the text ramp
    /// legible against them.
    /// </summary>
    /// <remarks>
    /// The slider moves <c>Background</c>, <c>Surface</c>, <c>Surface2</c> and <c>Border</c> by up to ±6%
    /// lightness. The text ramp is seeded once, so shifting the surfaces alone narrows every text/background
    /// pairing in the app: measured across the whole slider range, <c>TextMuted</c> fell below 4.5:1 in 12
    /// of 60 preset × position combinations, worst 3.94:1. The popup presents the slider as harmless
    /// personalisation, so the outcome has to be unreachable rather than merely discouraged.
    /// <para>Two simpler fixes were measured and rejected. Shifting the ramp by <c>-offset</c> makes it
    /// worse (13 of 60, worst 3.49) because it assumes the text is darker than its surface, which is only
    /// true on a light preset — on a dark one the text is the lighter of the two, so darkening it closes the
    /// gap it was meant to open. Clamping the slider cannot work either: the safe range is the lower half
    /// for dark presets and the upper half for light ones, so the intersection across all twelve is
    /// 0.50-0.55 and the feature would be gone.</para>
    /// <para>Pure and static so the whole reachable range can be swept in a unit test — the instance path
    /// writes into <c>Application.Current.Resources</c>, which a test has no business doing.</para>
    /// </remarks>
    internal static ThemePreset Shade(ThemePreset baseTheme, double position)
    {
        var offset = (position - 0.5) * 0.12;

        var shaded = baseTheme with
        {
            Background = ShiftLightness(baseTheme.Background, offset),
            Surface = ShiftLightness(baseTheme.Surface, offset),
            Surface2 = ShiftLightness(baseTheme.Surface2, offset),
            Border = ShiftLightness(baseTheme.Border, offset)
        };

        // TextPrimary is corrected first because the two DERIVED surfaces are lerped toward it, so they
        // cannot be computed until it is final. Derived here with the same factors Apply uses, from the
        // shaded Surface2 and the corrected primary — which is exactly the pair Apply will be handed.
        var primary = Legible(shaded.TextPrimary, baseTheme.IsDark, 7.0, shaded.Background);
        var surface3 = Lerp(shaded.Surface2, primary, 0.05);
        var surface4 = Lerp(shaded.Surface2, primary, 0.10);

        // The floors and the surfaces each one is measured against mirror ThemeTextContrastTests exactly:
        // this correction enforces the same contract those assertions check, rather than a second opinion
        // about what "legible" means.
        //
        // Surface3/Surface4 were missing from both sides of that mirror until #1555. Measured across all
        // twelve presets and twenty-one slider positions, the slider pushed muted text as low as 3.84:1 on
        // Surface4 — and on warm-sand it did so from the DEFAULT position outward, so this was reachable
        // without the user touching anything. Correcting against only the seeded rungs left the two rungs
        // that actually carry the worst contrast unprotected.
        return shaded with
        {
            TextPrimary = primary,
            TextSecondary = Legible(shaded.TextSecondary, baseTheme.IsDark, 4.5,
                                    shaded.Surface2, surface4),
            TextMuted = Legible(shaded.TextMuted, baseTheme.IsDark, 4.5,
                                shaded.Background, shaded.Surface, shaded.Surface2, surface3, surface4)
        };
    }

    /// <summary>
    /// Returns <paramref name="text"/> unchanged when it already clears <paramref name="minimum"/> against
    /// every surface, otherwise walks it away from them until it does.
    /// </summary>
    /// <remarks>
    /// "Away" is toward white on a dark theme and toward black on a light one, decided from the preset's own
    /// mode rather than by comparing luminances, so a surface shifted close to the text cannot flip the
    /// direction mid-walk. 2% steps to a 80% ceiling, matching <c>ChartTheme.ReadableAgainst</c>; every real
    /// preset clears its floor far earlier, and the ceiling exists so a pathological custom theme terminates
    /// rather than looping.
    /// </remarks>
    private static Color Legible(Color text, bool isDark, double minimum, params Color[] surfaces)
    {
        var target = isDark ? Colors.White : Colors.Black;

        for (var step = 0; step <= 40; step++)
        {
            var candidate = Lerp(text, target, step * 0.02);
            if (surfaces.All(surface => ContrastAgainst(candidate, surface) >= minimum))
                return candidate;
        }

        return Lerp(text, target, 0.8);
    }

    public void Apply(ThemePreset theme)
    {
        // No running app means no resource dictionary to repaint — a no-op, not a crash. This is the one
        // WPF touch-point in the service; guarding it lets Load/Initialize (and thus the persistence
        // seam) run in a headless unit test, and is harmless in production where Application.Current is
        // always set. Without it, every public entry point NPEs the moment it is reached off the UI app.
        if (Application.Current is null) return;

        var res = Application.Current.Resources;
        SetBrush(res, "Surface0", theme.Background);
        SetBrush(res, "Surface1", theme.Surface);
        SetBrush(res, "Surface2", theme.Surface2);
        SetBrush(res, "Surface3", Lerp(theme.Surface2, theme.TextPrimary, 0.05));
        var surface4 = Lerp(theme.Surface2, theme.TextPrimary, 0.1);
        SetBrush(res, "Surface4", surface4);
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

        // The foreground for anything drawn ON the accent fill: the primary button's label, the checked
        // mode pill's label, the checkbox tick. Those were hardcoded White, and the accent swings from
        // indigo #6366F1 to amber #F59E0B across the presets, so white measured 2.15:1 on warm-ember and
        // cleared AA on only two of the twelve.
        SetBrush(res, "TextOnAccent", OnColor(theme.Accent));

        // The OFF thumb of a toggle switch, drawn on the Surface4 track. It was hardcoded White, and
        // Surface4 is near-white on the six light presets, so the thumb measured 1.33 to 1.67:1 there —
        // the switch had no readable state at all, which is worse than a label at 2:1. The ON thumb is
        // not this brush: the IsChecked trigger turns the track into Accent, so it uses TextOnAccent
        // above. That split is load-bearing rather than tidy — a single brush left the ON state at
        // 2.15:1 on warm-ember and 2.54:1 on dark-forest, a defect the light-preset audit missed
        // because both are dark presets.
        SetBrush(res, "ToggleThumb", OnColor(surface4));

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

        SetBrush(res, "TextOnDanger", TextOnDanger(isDark));
    }

    /// <summary>
    /// The legible foreground for the Danger fill in the given mode, for <c>DangerButton</c>'s label.
    /// </summary>
    /// <remarks>
    /// Derived from the palette rather than listed beside it, so retuning Danger carries its paired
    /// foreground along instead of silently stranding it at a choice made for the old value.
    /// <para>It has to be per-mode: white on the dark-mode <c>#EF4444</c> is 3.76:1 — below the 4.5:1 a
    /// 13px SemiBold label needs — while white on the light-mode <c>#B91C1C</c> is 6.47:1 and correct.
    /// One constant cannot serve both.</para>
    /// </remarks>
    internal static Color TextOnDanger(bool isDark) =>
        OnColor(StatusPalette(isDark).First(entry => entry.Key == "Danger").Color);

    /// <summary>
    /// Black or white, whichever contrasts more against <paramref name="background"/> — the standard
    /// WCAG-driven "on colour" rule for text or a mark drawn on a filled surface.
    /// </summary>
    /// <remarks>
    /// Pure black, not a softened near-black. #1A1A1A was the first choice, because pure black can read
    /// as harsh on a saturated fill, but it does not clear the bar: against the default accent #6366F1 it
    /// measures 3.90:1 while white measures 4.47:1, so NEITHER reaches 4.5 and the app's own signature
    /// colour would still fail. With pure black every preset clears it, the worst case being 4.60:1.
    /// <para>Deliberately a local contrast implementation rather than one shared with the tests.
    /// <c>ThemeStatusBrushTests</c> and <c>ThemeTextContrastTests</c> keep their own, so a mistake in this
    /// formula shows up as a failing assertion instead of being cancelled out by a shared bug. The other
    /// copy, <c>ChartTheme.Contrast</c>, is typed for SkiaSharp's SKColor and belongs to the chart stack.</para>
    /// </remarks>
    internal static Color OnColor(Color background) =>
        ContrastAgainst(Colors.White, background) >= ContrastAgainst(Colors.Black, background)
            ? Colors.White
            : Colors.Black;

    /// <summary>WCAG 2.x relative-luminance contrast ratio between two opaque colours.</summary>
    private static double ContrastAgainst(Color a, Color b)
    {
        var (la, lb) = (RelativeLuminance(a), RelativeLuminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
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

            // Critical severity (LogsView): a distinct, hotter red than Danger. Dark values mirror the
            // previous hardcoded #FF3B30 literals so dark themes are unchanged; the light array darkens
            // them for AA legibility on near-white surfaces (the Critical card used to stay invisible).
            ("CriticalText", C("#FF3B30")), ("CriticalBgSubtle", C("#14FF3B30")),
            // WindowsUpdate category badges: Driver/.NET/Feature-upgrade have no semantic equivalent,
            // so they get their own per-preset brushes (Security/Defender/Servicing reuse Danger/Success/
            // Info text). Dark = the previous hardcoded Tailwind-300 tints; light darkened for AA.
            ("BadgeIndigoText", C("#A5B4FC")), ("BadgePurpleText", C("#D8B4FE")), ("BadgePinkText", C("#F9A8D4")),

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

            // Dashboard metric accents. The last two App.xaml brushes that were never re-themed, so a
            // light preset kept these dark-theme mid-tones while the sibling CPU card used the
            // mode-aware Success brush — one crisp card beside two washed-out ones. Dark values mirror
            // App.xaml exactly (no visual change on dark).
            ("MetricBlue", C("#3B82F6")), ("MetricPurple", C("#A855F7")),
        ]
        :
        [
            ("WarningText", C("#92400E")), ("WarningBgSubtle", C("#26FBBF24")),   // amber-800 text — AA on white
            ("WarningBg", C("#40FBBF24")), ("WarningStripe", C("#D97706")),
            ("SuccessText", C("#15803D")), ("SuccessBgSubtle", C("#2622C55E")), ("SuccessBorder", C("#5522C55E")),
            ("InfoText", C("#0369A1")), ("InfoBgSubtle", C("#2638BDF8")), ("InfoBorder", C("#5538BDF8")),
            ("DangerText", C("#B91C1C")), ("DangerBgSubtle", C("#26EF4444")), ("DangerBorder", C("#55EF4444")),

            // Critical severity (LogsView) — darker red than dark-mode so the Critical card/dot stay AA
            // on the tinted light surfaces (was a fixed #FF3B30 that washed out); BgSubtle bumped alpha.
            ("CriticalText", C("#B01508")), ("CriticalBgSubtle", C("#20FF3B30")),
            // Category badges darkened for AA on light presets (indigo-700 / purple-700 / pink-800).
            // pink-800 #9D174D (not pink-700) clears 4.5:1 on the most-tinted light surface #FBCFE8.
            ("BadgeIndigoText", C("#4338CA")), ("BadgePurpleText", C("#7E22CE")), ("BadgePinkText", C("#9D174D")),

            // Light: darker, saturated tones that meet WCAG AA as SMALL text — not just on pure white,
            // but on the most-tinted light preset card surface (soft-blossom Surface2 #FBCFE8 is the
            // worst case). Info #075985 / Success #166534 / Warning #9A3412 all clear 4.5:1 there;
            // Danger #B91C1C already did. (Earlier values passed on #FFFFFF but dipped to ~3.6-4.3 on
            // the pastel presets — see ThemeStatusBrushTests, which now asserts against the tinted surface.)
            ("Info", C("#075985")), ("Success", C("#166534")), ("Warning", C("#9A3412")), ("Danger", C("#B91C1C")),

            // Dashboard metric accents on light: blue-700 / purple-700, the same step already used for
            // BadgeIndigoText / BadgePurpleText above. These are NON-TEXT marks — a ProgressBar fill and
            // a 6px dot — so the bar is WCAG 1.4.11's 3:1 against their own Surface3 track, which the
            // frozen dark tints missed badly (MetricBlue read 2.42:1 on soft-blossom). -700 rather than
            // -800 keeps the hue identity: the 26px number must still read as "a blue metric", not black.
            ("MetricBlue", C("#1D4ED8")), ("MetricPurple", C("#7E22CE")),

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
            var dir = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(dir);
            var data = new ThemeSettings(CurrentPresetId, CurrentMode, ShadePosition,
                CurrentTheme.Accent.ToString(), CurrentTheme.Background.ToString(),
                CurrentTheme.Surface.ToString(), CurrentTheme.TextPrimary.ToString());
            var json = JsonSerializer.Serialize(data, JsonDefaults.Indented);
            AtomicFile.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex) { Log.Debug("Theme save failed: {Error}", ex.Message); }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var json = File.ReadAllText(_settingsPath);
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
            C("#F1F3F7"), C("#A3ADBF"), C("#9097A7")), // muted: WCAG AA on the DERIVED Surface3/Surface4 too (was #7B8396, 4.09 / 3.53)
        ["deep-ocean"] = new("deep-ocean", "Deep Ocean", true,
            C("#3B82F6"), C("#050D1A"), C("#0A1628"), C("#0F1D33"), C("#1A2D4D"),
            C("#E2E8F0"), C("#94A3B8"), C("#8D9AAC")), // muted: WCAG AA on the DERIVED Surface3/Surface4 too (was #78879B, 4.11 / 3.59)
        ["dark-forest"] = new("dark-forest", "Dark Forest", true,
            C("#10B981"), C("#020F0A"), C("#021A12"), C("#03261A"), C("#0A3D2A"),
            C("#D1FAE5"), C("#6EE7B7"), C("#34D399")),
        ["neon-rose"] = new("neon-rose", "Neon Rose", true,
            C("#EC4899"), C("#120508"), C("#1A0A0F"), C("#240E16"), C("#3D1525"),
            C("#FDF2F8"), C("#F9A8D4"), C("#F472B6")),
        ["violet-night"] = new("violet-night", "Violet Night", true,
            C("#A855F7"), C("#0A0515"), C("#0F0A1A"), C("#160F26"), C("#2D1B4E"),
            C("#F3E8FF"), C("#C4B5FD"), C("#A078F7")), // muted: WCAG AA on the DERIVED Surface3/Surface4 too (was #8E60F6, 4.14 / 3.62)
        ["warm-ember"] = new("warm-ember", "Warm Ember", true,
            C("#F59E0B"), C("#0F0A04"), C("#1A1008"), C("#24180C"), C("#3D2A12"),
            C("#FEF3C7"), C("#FCD34D"), C("#FBBF24")),
        ["clean-indigo"] = new("clean-indigo", "Clean Indigo", false,
            C("#6366F1"), C("#FFFFFF"), C("#F8FAFC"), C("#F1F5F9"), C("#E2E8F0"),
            C("#1E1B4B"), C("#4338CA"), C("#5153C6")), // muted: WCAG AA on the DERIVED Surface3/Surface4 too (was #5D5FE2, 4.14 / 3.74)
        ["sky-breeze"] = new("sky-breeze", "Sky Breeze", false,
            C("#0EA5E9"), C("#F8FAFC"), C("#F0F9FF"), C("#E0F2FE"), C("#BAE6FD"),
            // The only preset needing BOTH tiers re-seeded: secondary was 4.39 on Surface4 (was #0369A1).
            C("#0C4A6E"), C("#02669D"), C("#04679A")), // muted: WCAG AA on the DERIVED Surface3/Surface4 too (was #0572AB, 4.20 / 3.88)
        ["warm-sand"] = new("warm-sand", "Warm Sand", false,
            C("#D97706"), C("#FFFBEB"), C("#FEF3C7"), C("#FDE68A"), C("#FCD34D"),
            C("#451A03"), C("#78350F"), C("#92400E")),
        ["mint-fresh"] = new("mint-fresh", "Mint Fresh", false,
            C("#16A34A"), C("#F0FDF4"), C("#DCFCE7"), C("#BBF7D0"), C("#86EFAC"),
            C("#14532D"), C("#166534"), C("#136C34")), // muted: WCAG AA on the DERIVED Surface3/Surface4 too (was #15783A, 4.22 / 3.91)
        ["soft-blossom"] = new("soft-blossom", "Soft Blossom", false,
            C("#DB2777"), C("#FDF2F8"), C("#FCE7F3"), C("#FBCFE8"), C("#F9A8D4"),
            C("#500724"), C("#831843"), C("#9D174D")),
        ["lavender"] = new("lavender", "Lavender", false,
            C("#7C3AED"), C("#FAF5FF"), C("#F3E8FF"), C("#E9D5FF"), C("#D8B4FE"),
            C("#2E1065"), C("#4C1D95"), C("#5B21B6")),
    };

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
