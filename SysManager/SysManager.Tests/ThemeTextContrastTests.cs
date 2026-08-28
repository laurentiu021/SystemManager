// SysManager · ThemeTextContrastTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Media;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Contrast regression tests for the neutral TEXT ramp (TextMuted / TextSecondary) across every
/// built-in preset. Companion to <see cref="ThemeStatusBrushTests"/>, which covers the semantic
/// status brushes. The bug this pins: TextMuted was tuned by eye and, on six presets
/// (midnight-indigo, deep-ocean, violet-night, clean-indigo, sky-breeze, mint-fresh), fell below
/// WCAG AA (4.5:1) against the preset's lightest layered surface (Surface2 — the surface muted
/// labels most commonly sit on, e.g. DataGrid column headers). These assertions FAIL against the
/// old seeds and PASS after the fix. Every preset is checked so a future seed edit can't silently
/// reintroduce a sub-AA muted value.
/// </summary>
public class ThemeTextContrastTests
{
    public static IEnumerable<object[]> AllPresets =>
        ThemePreset.Defaults.Values.Select(p => new object[] { p.Id });

    // TextMuted is body/label text — WCAG AA for normal text is 4.5:1. It must clear that against
    // the WORST-CASE (lowest-contrast) layered surface it renders on. Surface3/Surface4 are derived
    // by Lerp toward TextPrimary (higher contrast for muted, so not the worst case); the seeded
    // Surface2 is the worst common case, so we assert against Background, Surface, and Surface2.
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void TextMuted_MeetsWcagAa_OnEveryLayeredSurface(string presetId)
    {
        var p = ThemePreset.Defaults[presetId];
        foreach (var (label, surface) in new[]
                 {
                     ("Background", p.Background),
                     ("Surface", p.Surface),
                     ("Surface2", p.Surface2),
                 })
        {
            var ratio = ContrastRatio(p.TextMuted, surface);
            Assert.True(ratio >= 4.5,
                $"Preset '{presetId}': TextMuted must meet WCAG AA (4.5:1) on {label}; got {ratio:F2}:1.");
        }
    }

    // TextSecondary is the next tier up and should comfortably clear AA everywhere; assert it too so
    // a future palette tweak can't quietly regress it below the muted tier.
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void TextSecondary_MeetsWcagAa_OnSurface2(string presetId)
    {
        var p = ThemePreset.Defaults[presetId];
        var ratio = ContrastRatio(p.TextSecondary, p.Surface2);
        Assert.True(ratio >= 4.5,
            $"Preset '{presetId}': TextSecondary must meet WCAG AA (4.5:1) on Surface2; got {ratio:F2}:1.");
    }

    // TextPrimary is the highest tier — hold it to the stricter AAA bar (7:1) on the base background,
    // matching the design brief's "don't go pure white — harsh, but must stay AAA" intent.
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void TextPrimary_MeetsWcagAaa_OnBackground(string presetId)
    {
        var p = ThemePreset.Defaults[presetId];
        var ratio = ContrastRatio(p.TextPrimary, p.Background);
        Assert.True(ratio >= 7.0,
            $"Preset '{presetId}': TextPrimary must meet WCAG AAA (7:1) on Background; got {ratio:F2}:1.");
    }

    // ── The OS title bar follows the preset ─────────────────────────────────
    // MainWindow tells DWM whether to draw the title bar dark via DWMWA_USE_IMMERSIVE_DARK_MODE, and
    // it used to pass a hardcoded 1 exactly once at startup. So on the six light presets the app was
    // a near-white window wearing a black title bar — the last surface still pinned to dark after
    // every brush and control template had been migrated. The DWM call needs a real window, so what
    // is asserted here is the SIGNAL that drives it: IsDark must classify every preset correctly, or
    // the fix would confidently send the wrong value.

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Preset_IsDark_AgreesWithItsOwnBackground(string presetId)
    {
        var p = ThemePreset.Defaults[presetId];
        // 384 is the threshold the record itself applies to CUSTOM themes, so the built-ins must be
        // consistent with it — otherwise a built-in and a custom theme of the same shade disagree.
        var sum = p.Background.R + p.Background.G + p.Background.B;
        Assert.Equal(sum < 384, p.IsDark);
    }

    [Fact]
    public void BothDarkAndLightPresetsShip()
    {
        // If every preset were dark the title-bar bug could not manifest and the theory above would
        // be silently vacuous. Pin that both kinds exist so it stays meaningful.
        var dark = ThemePreset.Defaults.Values.Count(p => p.IsDark);
        var light = ThemePreset.Defaults.Values.Count(p => !p.IsDark);
        Assert.True(dark > 0, "No dark presets — the title-bar theory would be vacuous.");
        Assert.True(light > 0, "No light presets — the title-bar bug could not manifest.");
    }

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
    // ── The shade slider's whole range (#1558) ─────────────────────────────────

    /// <summary>
    /// Every position the background-shade slider can reach keeps the text ramp at its floor.
    /// </summary>
    /// <remarks>
    /// The theories above read <c>ThemePreset.Defaults[presetId]</c>, which is the seeded preset — shade
    /// 0.5. The slider moves the surfaces by up to ±6% lightness while the ramp is seeded once, so the
    /// entire shifted range was untested, and measured before the fix it put <c>TextMuted</c> below 4.5:1 in
    /// 12 of 60 preset × position combinations (worst 3.94:1, midnight-indigo at 1.0).
    /// <para>Swept at 0.05 rather than at a handful of points: the failures cluster at the ends, and a
    /// five-point sweep would step over a preset whose floor is breached in between.</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void EveryShadePosition_KeepsTheTextRampLegible(string presetId)
    {
        var seeded = ThemePreset.Defaults[presetId];
        var offenders = new List<string>();
        var positionsChecked = 0;

        for (var step = 0; step <= 20; step++)
        {
            var position = step * 0.05;
            var shaded = ThemeService.Shade(seeded, position);
            positionsChecked++;

            foreach (var (label, surface) in new[]
                     {
                         ("Background", shaded.Background),
                         ("Surface", shaded.Surface),
                         ("Surface2", shaded.Surface2),
                     })
            {
                var muted = ContrastRatio(shaded.TextMuted, surface);
                if (muted < 4.5)
                    offenders.Add($"shade {position:F2}: TextMuted on {label} = {muted:F2}:1");
            }

            var secondary = ContrastRatio(shaded.TextSecondary, shaded.Surface2);
            if (secondary < 4.5)
                offenders.Add($"shade {position:F2}: TextSecondary on Surface2 = {secondary:F2}:1");

            var primary = ContrastRatio(shaded.TextPrimary, shaded.Background);
            if (primary < 7.0)
                offenders.Add($"shade {position:F2}: TextPrimary on Background = {primary:F2}:1 (AAA)");
        }

        // Vacuity floor: a loop that stopped running would report a clean sweep of nothing.
        Assert.Equal(21, positionsChecked);

        Assert.True(offenders.Count == 0,
            $"Preset '{presetId}' drops below its contrast floor at some slider positions. The shade slider "
            + "is presented as harmless personalisation, so an unreadable outcome must be unreachable rather "
            + $"than merely unlikely:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// At the default position the correction must be a no-op, so shipping it changes nothing for anyone who
    /// has not touched the slider.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void TheDefaultShadePosition_LeavesTheSeededRampUntouched(string presetId)
    {
        var seeded = ThemePreset.Defaults[presetId];
        var shaded = ThemeService.Shade(seeded, 0.5);

        Assert.Equal(seeded.TextPrimary, shaded.TextPrimary);
        Assert.Equal(seeded.TextSecondary, shaded.TextSecondary);
        Assert.Equal(seeded.TextMuted, shaded.TextMuted);
        Assert.Equal(seeded.Background, shaded.Background);
    }

    /// <summary>
    /// The correction has to actually engage somewhere, or the sweep above would pass simply because nothing
    /// ever breaches a floor and the whole mechanism could be deleted unnoticed.
    /// </summary>
    [Fact]
    public void TheCorrection_EngagesWhereTheSliderWouldOtherwiseBreachTheFloor()
    {
        // midnight-indigo at the top of the range is the worst measured case: 3.94:1 before the fix.
        var seeded = ThemePreset.Defaults["midnight-indigo"];
        var shaded = ThemeService.Shade(seeded, 1.0);

        Assert.NotEqual(seeded.TextMuted, shaded.TextMuted);
        Assert.True(ContrastRatio(shaded.TextMuted, shaded.Surface2) >= 4.5);
        Assert.True(ContrastRatio(seeded.TextMuted, shaded.Surface2) < 4.5,
            "the uncorrected ramp was expected to fail against the shifted surface — if it now passes, "
            + "this test no longer proves the correction does anything.");
    }
}
