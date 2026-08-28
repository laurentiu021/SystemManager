// SysManager · ThemeStatusBrushTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Media;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Regression tests for <see cref="ThemeService.StatusPalette"/>. The bug: the semantic status
/// brushes (WarningText #FCD34D, SuccessText, InfoText, DangerText) were static in App.xaml and
/// calibrated for dark surfaces; <see cref="ThemeService.Apply"/> repainted Surface/Text/Accent per
/// preset but NEVER these, so on the six LIGHT presets the pale warning/info text rendered on a
/// near-white banner and failed WCAG contrast (e.g. #FCD34D on #FFFFFF ≈ 1.4:1 — illegible).
/// These tests pin that the light palette uses dark, saturated text that meets WCAG AA (4.5:1),
/// while the dark palette stays legible on dark surfaces. They FAIL against the old static colors.
/// </summary>
public class ThemeStatusBrushTests
{
    // The brightest light-preset surface a status banner layers over (clean-indigo Surface = #FFFFFF)
    // and the darkest dark-preset base (midnight-indigo Background = #070A0F). Text must contrast
    // against the worst-case surface for its mode.
    private static readonly Color LightSurface = C("#FFFFFF");
    private static readonly Color DarkSurface = C("#070A0F");

    // The WORST-CASE light card surface: the most-tinted layered surface across all light presets
    // (soft-blossom Surface2 = #FBCFE8). Small semantic/console text renders on the tinted Surface1/2
    // of the coloured presets, NOT on pure white — asserting only against #FFFFFF let sub-AA values
    // through on the pastel presets. This is the real worst case for the base + console tones.
    private static readonly Color LightTintedSurface = C("#FBCFE8");

    [Theory]
    [InlineData("WarningText")]
    [InlineData("SuccessText")]
    [InlineData("InfoText")]
    [InlineData("DangerText")]
    public void LightPalette_StatusText_MeetsWcagAaOnWhite(string key)
    {
        var color = Lookup(ThemeService.StatusPalette(isDark: false), key);
        var ratio = ContrastRatio(color, LightSurface);
        Assert.True(ratio >= 4.5,
            $"Light-theme {key} must meet WCAG AA (4.5:1) against the near-white banner surface; got {ratio:F2}:1.");
    }

    [Theory]
    [InlineData("WarningText")]
    [InlineData("SuccessText")]
    [InlineData("InfoText")]
    [InlineData("DangerText")]
    public void DarkPalette_StatusText_StaysLegibleOnDark(string key)
    {
        var color = Lookup(ThemeService.StatusPalette(isDark: true), key);
        var ratio = ContrastRatio(color, DarkSurface);
        Assert.True(ratio >= 4.5,
            $"Dark-theme {key} must stay legible (4.5:1) on the dark surface; got {ratio:F2}:1.");
    }

    [Fact]
    public void LightAndDark_ProduceDifferentWarningText()
    {
        // The whole point of the fix: the two modes must diverge. If they're equal, the palette was
        // never actually mode-aware and the light-theme regression would silently return.
        var light = Lookup(ThemeService.StatusPalette(false), "WarningText");
        var dark = Lookup(ThemeService.StatusPalette(true), "WarningText");
        Assert.NotEqual(dark, light);
    }

    [Fact]
    public void Palette_CoversAllFourStatusTextKeys_InBothModes()
    {
        foreach (var mode in new[] { true, false })
        {
            var keys = ThemeService.StatusPalette(mode).Select(p => p.Key).ToHashSet();
            Assert.Contains("WarningText", keys);
            Assert.Contains("SuccessText", keys);
            Assert.Contains("InfoText", keys);
            Assert.Contains("DangerText", keys);
        }
    }

    // Base semantic brushes (Info/Success/Warning/Danger) are used directly as small-text Foreground
    // across the app (e.g. Cleanup's TEMP-folders stat). They were static App.xaml resources that never
    // recomputed per mode, so their light-cyan/green/amber/red washed out on near-white light surfaces.
    // Now theme-derived — pin AA on white for light, legible on dark for dark.
    [Theory]
    [InlineData("Info")]
    [InlineData("Success")]
    [InlineData("Warning")]
    [InlineData("Danger")]
    public void LightPalette_BaseSemantic_MeetsWcagAaOnTintedSurface(string key)
    {
        // Assert against the most-tinted light surface (not just white) — that is where these tones
        // actually render as small text on the coloured presets, and where they previously dipped sub-AA.
        var ratio = ContrastRatio(Lookup(ThemeService.StatusPalette(false), key), LightTintedSurface);
        Assert.True(ratio >= 4.5,
            $"Light-theme base '{key}' brush must meet WCAG AA (4.5:1) as small text on the most-tinted light card surface; got {ratio:F2}:1.");
    }

    // Console output palette (Out*Brush) — same class: static-only before, so light-theme consoles
    // rendered near-invisible pale-grey body text on a near-white card. OutOutput/OutVerbose are neutral
    // text tones; the semantic console lines reuse the AA light tones. All must clear AA on white for light.
    [Theory]
    [InlineData("OutOutputBrush")]
    [InlineData("OutVerboseBrush")]
    [InlineData("OutInfoBrush")]
    [InlineData("OutWarnBrush")]
    [InlineData("OutErrorBrush")]
    [InlineData("OutDebugBrush")]
    [InlineData("OutProgressBrush")]
    public void LightPalette_ConsolePalette_MeetsWcagAaOnTintedSurface(string key)
    {
        var ratio = ContrastRatio(Lookup(ThemeService.StatusPalette(false), key), LightTintedSurface);
        Assert.True(ratio >= 4.5,
            $"Light-theme console '{key}' must meet WCAG AA (4.5:1) on the most-tinted light console card; got {ratio:F2}:1.");
    }

    [Fact]
    public void BaseAndConsole_LightAndDark_Diverge()
    {
        // Mode-awareness guard: if a base/console brush is identical in both modes it was never
        // recomputed and the light regression would silently return.
        foreach (var key in new[] { "Info", "Success", "Warning", "Danger", "OutOutputBrush", "OutInfoBrush" })
            Assert.NotEqual(Lookup(ThemeService.StatusPalette(true), key),
                            Lookup(ThemeService.StatusPalette(false), key));
    }

    // Critical (LogsView) + category-badge text brushes added when those views were migrated off
    // hardcoded pale dark-theme tints. Same regression class as the semantic brushes above: pin AA
    // on the tinted light surface and mode-divergence so the light-theme washout can't return.
    [Theory]
    [InlineData("CriticalText")]
    [InlineData("BadgeIndigoText")]
    [InlineData("BadgePurpleText")]
    [InlineData("BadgePinkText")]
    public void LightPalette_CriticalAndBadgeText_MeetsWcagAaOnTintedSurface(string key)
    {
        var ratio = ContrastRatio(Lookup(ThemeService.StatusPalette(false), key), LightTintedSurface);
        Assert.True(ratio >= 4.5,
            $"Light-theme '{key}' must meet WCAG AA (4.5:1) as small badge text on the most-tinted light surface; got {ratio:F2}:1.");
    }

    /// <summary>
    /// Mirrors <c>ThemeService.Lerp</c> so the track colour under a metric fill is derived with the
    /// same maths the app uses, rather than an approximation of it.
    /// </summary>
    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    // Dashboard metric accents (#1623). Deliberately NOT folded into the theories above: both real
    // usages are non-text — the ProgressBar fill and the 6px status dot — so the applicable bar is
    // WCAG 1.4.11's 3:1 for graphical objects, not 4.5:1 for small text. Asserting 4.5 here would be
    // over-strict for a fill and would reject a legitimate future value; asserting nothing would leave
    // the defect free to return. The 26px bold percentage is large text, which 3:1 also covers.
    //
    // The surface is the fill's OWN track (Surface3), derived the way ThemeService derives it, because
    // that is what the fill is measured against — not the card behind it.
    [Theory]
    [InlineData("MetricBlue")]
    [InlineData("MetricPurple")]
    public void LightPalette_MetricAccents_MeetNonTextContrastOnTheirTrack(string key)
    {
        var track = Lerp(LightTintedSurface, C("#0F172A"), 0.05);   // Surface3 = Lerp(Surface2, TextPrimary, 0.05)
        var ratio = ContrastRatio(Lookup(ThemeService.StatusPalette(false), key), track);
        Assert.True(ratio >= 3.0,
            $"Light-theme '{key}' is a ProgressBar fill and a status dot, so it must meet WCAG 1.4.11 "
            + $"(3:1) against its own Surface3 track; got {ratio:F2}:1.");
    }

    [Theory]
    [InlineData("MetricBlue")]
    [InlineData("MetricPurple")]
    public void DarkPalette_MetricAccents_AreByteIdenticalToTheAppXamlDefaults(string key)
    {
        // The dark branch must be a no-op: these tints already worked there, and a shift would
        // invalidate every dark-theme screenshot for no benefit.
        var expected = key == "MetricBlue" ? C("#3B82F6") : C("#A855F7");
        Assert.Equal(expected, Lookup(ThemeService.StatusPalette(true), key));
    }

    [Theory]
    [InlineData("MetricBlue")]
    [InlineData("MetricPurple")]
    public void MetricAccents_PresentInBothModes_AndDiverge(string key)
    {
        // If the light rows were dropped, Lookup would throw rather than silently fall back, and the
        // divergence assertion states the intent that light is NOT simply the dark value.
        Assert.NotEqual(Lookup(ThemeService.StatusPalette(true), key),
                        Lookup(ThemeService.StatusPalette(false), key));
    }

    [Theory]
    [InlineData("CriticalText")]
    [InlineData("BadgeIndigoText")]
    [InlineData("BadgePurpleText")]
    [InlineData("BadgePinkText")]
    public void DarkPalette_CriticalAndBadgeText_StaysLegibleOnDark(string key)
    {
        var ratio = ContrastRatio(Lookup(ThemeService.StatusPalette(true), key), DarkSurface);
        Assert.True(ratio >= 4.5,
            $"Dark-theme '{key}' must stay legible (4.5:1) on the dark surface; got {ratio:F2}:1.");
    }

    [Fact]
    public void CriticalAndBadge_PresentInBothModes_AndDiverge()
    {
        foreach (var key in new[] { "CriticalText", "CriticalBgSubtle", "BadgeIndigoText", "BadgePurpleText", "BadgePinkText" })
        {
            Assert.Contains(key, ThemeService.StatusPalette(true).Select(p => p.Key));
            Assert.Contains(key, ThemeService.StatusPalette(false).Select(p => p.Key));
        }
        // The text tones must differ per mode (the whole point of migrating off the fixed literal).
        foreach (var key in new[] { "CriticalText", "BadgeIndigoText", "BadgePurpleText", "BadgePinkText" })
            Assert.NotEqual(Lookup(ThemeService.StatusPalette(true), key),
                            Lookup(ThemeService.StatusPalette(false), key));
    }

    // ── StatusColors: the string-hex path that was never migrated ───────────
    // The *ColorHex properties (disk health, temperature, health score, tune-up, Cleanup verdicts,
    // network health) fed HexToBrushConverter with dark-calibrated `const` hex. A const is baked in
    // at compile time, so ThemeService could never recompute it — the identical regression this file
    // already guards for the DynamicResource brushes, on the one route that bypassed them. They now
    // carry a theme resource KEY instead, which is what these tests pin.

    // Elevated intentionally aliases Warning (see StatusColors), so it is not a separate row —
    // xUnit rejects duplicate InlineData. StatusColors_ElevatedAliasesWarning pins that on purpose.
    [Theory]
    [InlineData(StatusColors.Good)]
    [InlineData(StatusColors.Warning)]
    [InlineData(StatusColors.Info)]
    [InlineData(StatusColors.Bad)]
    [InlineData(StatusColors.Neutral)]
    public void StatusColors_NameAThemedBrush_NotALiteral(string key)
    {
        // A '#' here means someone reintroduced a hardcoded colour, which is unthemeable by
        // construction — the exact defect. Fail loudly rather than let it ship.
        Assert.False(key.StartsWith('#'),
            $"StatusColors must name a theme resource, not a literal colour; got '{key}'.");
    }

    [Theory]
    [InlineData(StatusColors.Good)]
    [InlineData(StatusColors.Warning)]
    [InlineData(StatusColors.Info)]
    [InlineData(StatusColors.Bad)]
    public void StatusColors_ResolveInBothStatusPalettes(string key)
    {
        // A key ThemeService does not emit would resolve to nothing at runtime and the verdict text
        // would silently fall back to grey — worse than the bug, because it looks deliberate.
        foreach (var isDark in new[] { true, false })
            Assert.Contains(key, ThemeService.StatusPalette(isDark).Select(p => p.Key));
    }

    [Fact]
    public void StatusColors_Neutral_IsAPerPresetThemeBrush()
    {
        // Neutral maps to TextMuted, which Apply writes from the preset (ThemeService.cs SetBrush
        // "TextMuted", theme.TextMuted) rather than from StatusPalette — so it is themed, just not
        // via that list. Assert the property it actually comes from varies across presets, which is
        // the property that matters: a single fixed grey is what the old #9AA0A6 constant was.
        Assert.Equal("TextMuted", StatusColors.Neutral);
        var muted = ThemePreset.Defaults.Values.Select(p => p.TextMuted).Distinct().Count();
        Assert.True(muted > 1, $"TextMuted must differ across presets to be theme-aware; found {muted} distinct value(s).");
    }

    [Theory]
    [InlineData(StatusColors.Good)]
    [InlineData(StatusColors.Warning)]
    [InlineData(StatusColors.Info)]
    [InlineData(StatusColors.Bad)]
    public void StatusColors_MeetWcagAaOnTheTintedLightSurface(string key)
    {
        // The measured failure: on a light preset the old constants ran 1.8:1 to 3.2:1 against the
        // light card — a pale smear exactly where "is my PC OK?" gets answered.
        var ratio = ContrastRatio(Lookup(ThemeService.StatusPalette(false), key), LightTintedSurface);
        Assert.True(ratio >= 4.5,
            $"Light-theme status colour '{key}' must meet WCAG AA (4.5:1) as small verdict text on the most-tinted light card; got {ratio:F2}:1.");
    }

    [Theory]
    [InlineData(StatusColors.Good)]
    [InlineData(StatusColors.Warning)]
    [InlineData(StatusColors.Info)]
    [InlineData(StatusColors.Bad)]
    public void StatusColors_StayLegibleOnDark(string key)
    {
        var ratio = ContrastRatio(Lookup(ThemeService.StatusPalette(true), key), DarkSurface);
        Assert.True(ratio >= 4.5,
            $"Dark-theme status colour '{key}' must stay legible (4.5:1) on the dark surface; got {ratio:F2}:1.");
    }

    [Fact]
    public void StatusColors_DivergeBetweenModes()
    {
        // Mode-awareness guard, as for the brushes above: identical values in both modes would mean
        // the colour is not actually recomputed and the light-theme washout could silently return.
        foreach (var key in new[] { StatusColors.Good, StatusColors.Warning, StatusColors.Info, StatusColors.Bad })
            Assert.NotEqual(Lookup(ThemeService.StatusPalette(true), key),
                            Lookup(ThemeService.StatusPalette(false), key));
    }

    [Fact]
    public void NoProducer_EmitsAHardcodedColour()
    {
        // The mechanical guard. Rewriting the palette to keys was a migration across 8 producers and
        // 13 test files, and the first attempt MISSED several: my sweep only matched
        // Assert.Equal("#..."), so hex passed via [InlineData] survived and CI caught it. Worse,
        // FriendlyEventEntry turned out to be a THIRD producer with its own drifted palette that the
        // issue never mentioned. A grep-style assertion is the only thing that makes "no literals
        // left" checkable instead of remembered.
        //
        // Every *ColorHex-style value the app can emit is enumerated here through its real code path.
        var emitted = new List<(string Where, string Value)>
        {
            ("HealthScoreResult(100)", new HealthScoreResult { Score = 100 }.ColorHex),
            ("HealthScoreResult(60)",  new HealthScoreResult { Score = 60 }.ColorHex),
            ("HealthScoreResult(10)",  new HealthScoreResult { Score = 10 }.ColorHex),
            ("Severity.Critical", new FriendlyEventEntry { Severity = EventSeverity.Critical }.SeverityColor),
            ("Severity.Error",    new FriendlyEventEntry { Severity = EventSeverity.Error }.SeverityColor),
            ("Severity.Warning",  new FriendlyEventEntry { Severity = EventSeverity.Warning }.SeverityColor),
            ("Severity.Info",     new FriendlyEventEntry { Severity = EventSeverity.Info }.SeverityColor),
            ("Severity.Verbose",  new FriendlyEventEntry { Severity = EventSeverity.Verbose }.SeverityColor),
            // These two are DEFAULTS/fallbacks rather than assertions, so no grep of the test suite
            // could ever have found them — CI did, on the second attempt.
            ("HealthDiagnostic default", new HealthDiagnostic().ColorHex),
            ("TemperatureReading(null)", new TemperatureReading("CPU", "Package", null).ColorHex),
            ("TemperatureReading(40)",   new TemperatureReading("CPU", "Package", 40).ColorHex),
            ("TemperatureReading(70)",   new TemperatureReading("CPU", "Package", 70).ColorHex),
            ("TemperatureReading(95)",   new TemperatureReading("CPU", "Package", 95).ColorHex),
        };

        var literals = emitted.Where(e => e.Value.StartsWith('#')).ToList();
        Assert.True(literals.Count == 0,
            "These still emit a hardcoded colour, which ThemeService cannot repaint: " +
            string.Join(", ", literals.Select(l => $"{l.Where} => {l.Value}")));
    }

    [Fact]
    public void StatusColors_ElevatedAliasesWarning()
    {
        // Deliberate: there is no separate "elevated" brush, and amber is the honest reading of
        // "worse than fine, not yet failing". Pinned so the aliasing is a decision, not an accident —
        // the old value was a light red that on a light surface was both illegible and easy to
        // mistake for the failure colour.
        Assert.Equal(StatusColors.Warning, StatusColors.Elevated);
    }

    [Fact]
    public void StatusColors_GoodAndBad_AreDifferentColoursInBothModes()
    {
        // "Healthy" and "failing" are the two readings the persona acts on, so they must never be
        // the same colour. Deliberately NOT a WCAG contrast check: that formula measures relative
        // luminance, and the light palette's Success #166534 and Danger #B91C1C are about equally
        // dark (1.10:1) while being obviously different hues. Compare the channels instead.
        // Elevated shares Warning's brush by design, so it is not compared here.
        foreach (var isDark in new[] { true, false })
        {
            var good = Lookup(ThemeService.StatusPalette(isDark), StatusColors.Good);
            var bad = Lookup(ThemeService.StatusPalette(isDark), StatusColors.Bad);

            // Manhattan distance in RGB: crude, but enough to catch "these became the same colour",
            // which is the only failure mode worth guarding here.
            var distance = Math.Abs(good.R - bad.R) + Math.Abs(good.G - bad.G) + Math.Abs(good.B - bad.B);
            Assert.True(distance >= 120,
                $"{(isDark ? "Dark" : "Light")}: Good {good} and Bad {bad} must read as different colours; RGB distance {distance}.");
        }
    }

    private static Color Lookup(IReadOnlyList<(string Key, Color Color)> palette, string key)
        => palette.First(p => p.Key == key).Color;

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelLum(a), lb = RelLum(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelLum(Color c)
    {
        static double Ch(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }
}
