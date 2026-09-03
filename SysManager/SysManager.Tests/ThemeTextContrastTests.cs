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

    // TextMuted is body/label text — WCAG AA for normal text is 4.5:1 — and it must clear that on the
    // WORST-CASE layered surface it renders on, which is Surface4, not Surface2.
    //
    // This check used to stop at Surface2 on the reasoning that "Surface3/Surface4 are derived by Lerp
    // toward TextPrimary (higher contrast for muted, so not the worst case)". That is backwards.
    // Lerping the SURFACE toward the text colour moves the surface CLOSER to the text, so contrast
    // DROPS: Surface4 is the lowest-contrast surface in the ramp, and it was the one left unasserted.
    // Six of twelve presets were sub-AA there — including midnight-indigo, the default — while this
    // theory reported the ramp clean (#1555). Fourteen places in the app put muted text on those two
    // surfaces, among them the Tweaks Hub "DEFAULT" badge at FontSize 10 and the elevation badge.
    //
    // The derived surfaces are computed here with the same Lerp factors ThemeService.Apply uses, so the
    // test and the service cannot drift to different definitions of the same colour.
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void TextMuted_MeetsWcagAa_OnEveryLayeredSurface(string presetId)
    {
        var p = ThemePreset.Defaults[presetId];
        var surface3 = Lerp(p.Surface2, p.TextPrimary, 0.05);
        var surface4 = Lerp(p.Surface2, p.TextPrimary, 0.10);

        // Non-vacuity floor. If the lerp factors were ever zeroed here, the two rows below would silently
        // become repeats of Surface2 and this theory would go back to covering nothing above it — passing,
        // which is exactly how the gap survived the first time.
        Assert.NotEqual(p.Surface2, surface3);
        Assert.NotEqual(surface3, surface4);

        foreach (var (label, surface) in new[]
                 {
                     ("Background", p.Background),
                     ("Surface", p.Surface),
                     ("Surface2", p.Surface2),
                     ("Surface3 (derived)", surface3),
                     ("Surface4 (derived)", surface4),
                 })
        {
            var ratio = ContrastRatio(p.TextMuted, surface);
            Assert.True(ratio >= 4.5,
                $"Preset '{presetId}': TextMuted must meet WCAG AA (4.5:1) on {label}; got {ratio:F2}:1. "
                + "Surface3 and Surface4 are DERIVED by lerping Surface2 toward TextPrimary, which lowers "
                + "contrast — so a muted colour that only just clears 4.5:1 on Surface2 will fail here. "
                + "Move TextMuted away from the surface (lighter on a dark preset, darker on a light one) "
                + "rather than adjusting the lerp, which is what gives the ramp its elevation.");
        }
    }

    // TextSecondary is the next tier up and should comfortably clear AA everywhere; assert it too so
    // a future palette tweak can't quietly regress it below the muted tier. Held to Surface4 for the
    // same reason as the muted tier above — sky-breeze sat at 4.39:1 there while passing on Surface2.
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void TextSecondary_MeetsWcagAa_OnSurface2(string presetId)
    {
        var p = ThemePreset.Defaults[presetId];
        foreach (var (label, surface) in new[]
                 {
                     ("Surface2", p.Surface2),
                     ("Surface4 (derived)", Lerp(p.Surface2, p.TextPrimary, 0.10)),
                 })
        {
            var ratio = ContrastRatio(p.TextSecondary, surface);
            Assert.True(ratio >= 4.5,
                $"Preset '{presetId}': TextSecondary must meet WCAG AA (4.5:1) on {label}; got {ratio:F2}:1.");
        }
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

            // Includes the DERIVED surfaces, for the same reason the seeded check above does: they are the
            // lowest-contrast rungs of the ramp, and the slider shifts Surface2 underneath them.
            var shadedSurface3 = Lerp(shaded.Surface2, shaded.TextPrimary, 0.05);
            var shadedSurface4 = Lerp(shaded.Surface2, shaded.TextPrimary, 0.10);

            foreach (var (label, surface) in new[]
                     {
                         ("Background", shaded.Background),
                         ("Surface", shaded.Surface),
                         ("Surface2", shaded.Surface2),
                         ("Surface3 (derived)", shadedSurface3),
                         ("Surface4 (derived)", shadedSurface4),
                     })
            {
                var muted = ContrastRatio(shaded.TextMuted, surface);
                if (muted < 4.5)
                    offenders.Add($"shade {position:F2}: TextMuted on {label} = {muted:F2}:1");
            }

            foreach (var (label, surface) in new[]
                     {
                         ("Surface2", shaded.Surface2),
                         ("Surface4 (derived)", shadedSurface4),
                     })
            {
                var secondary = ContrastRatio(shaded.TextSecondary, surface);
                if (secondary < 4.5)
                    offenders.Add($"shade {position:F2}: TextSecondary on {label} = {secondary:F2}:1");
            }

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
        // Measured against the DERIVED Surface4, because that is the rung the slider actually breaches.
        //
        // This test used to point at Surface2, where midnight-indigo measured 3.94:1 uncorrected. #1555
        // re-seeded that preset's muted colour for the derived surfaces, which lifted Surface2 to 5.11:1 —
        // so the example stopped breaching, the correction stopped engaging for the reason being asserted,
        // and the test's own third assertion fired to say it no longer proved anything. It was right. The
        // breach did not go away, it just moved down the ramp: the same preset at the same slider position
        // measures 3.84:1 on Surface4.
        var seeded = ThemePreset.Defaults["midnight-indigo"];
        var shaded = ThemeService.Shade(seeded, 1.0);
        var shadedSurface4 = Lerp(shaded.Surface2, shaded.TextPrimary, 0.10);

        Assert.NotEqual(seeded.TextMuted, shaded.TextMuted);
        Assert.True(ContrastRatio(shaded.TextMuted, shadedSurface4) >= 4.5);
        Assert.True(ContrastRatio(seeded.TextMuted, shadedSurface4) < 4.5,
            "the uncorrected ramp was expected to fail against the shifted surface — if it now passes, "
            + "this test no longer proves the correction does anything.");
    }
    // ── The toggle switch has to be readable in BOTH states ─────────────────
    // The thumb was hardcoded White and the IsChecked trigger moved it without changing its brush, so
    // one colour had to work on two different backgrounds and failed on each for opposite presets. OFF
    // sits on Surface4, which is Lerp(Surface2, TextPrimary, 0.1) — near-white on the six light presets,
    // where a white thumb measured 1.33-1.67:1 and the switch had no readable state at all. ON sits on
    // Accent, where white measured 2.15:1 on warm-ember and 2.54:1 on dark-forest.
    //
    // Two theories rather than one combined assertion: the halves fail on disjoint sets of presets, so a
    // single check could stay green with one half regressed while the other carried it.
    //
    // 3:1 is WCAG 1.4.11 for a non-text UI component. The real margins are far wider (12.32-15.76 OFF,
    // 4.60-9.78 ON), so this floor exists to catch a future seed edit rather than to be scraped past.

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void ToggleThumb_IsReadableOnItsTrack_WhenOff(string presetId)
    {
        var p = ThemePreset.Defaults[presetId];
        var track = Lerp(p.Surface2, p.TextPrimary, 0.1);
        var thumb = ThemeService.OnColor(track);

        var ratio = ContrastRatio(thumb, track);
        Assert.True(ratio >= 3.0,
            $"Preset '{presetId}': the OFF toggle thumb must clear 3:1 against its Surface4 track; "
            + $"got {ratio:F2}:1. A white thumb was 1.33:1 here before this rule existed.");
    }

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void ToggleThumb_IsReadableOnTheAccent_WhenOn(string presetId)
    {
        var p = ThemePreset.Defaults[presetId];
        // The ON thumb deliberately uses TextOnAccent — the same brush as the primary button's label —
        // because the IsChecked trigger turns the track into Accent.
        var thumb = ThemeService.OnColor(p.Accent);

        var ratio = ContrastRatio(thumb, p.Accent);
        Assert.True(ratio >= 3.0,
            $"Preset '{presetId}': the ON toggle thumb must clear 3:1 against the Accent track; "
            + $"got {ratio:F2}:1. A white thumb was 2.15:1 on warm-ember before this rule existed.");
    }

    [Fact]
    public void AWhiteToggleThumb_WouldStillFail_OnBothCounts()
    {
        // Positive control. Both theories above would pass trivially if OnColor ever returned white
        // everywhere, so this pins that the OLD value genuinely fails — on the light presets when off,
        // and on warm-ember and dark-forest when on. If either of these stops failing, the theories
        // above have stopped proving anything.
        var white = System.Windows.Media.Colors.White;

        var light = ThemePreset.Defaults["clean-indigo"];
        var lightTrack = Lerp(light.Surface2, light.TextPrimary, 0.1);
        Assert.True(ContrastRatio(white, lightTrack) < 3.0,
            "a white OFF thumb on clean-indigo's track was the defect; if it now clears 3:1 the surface "
            + "ramp changed and these rules need re-deriving.");

        foreach (var id in new[] { "warm-ember", "dark-forest" })
        {
            var accent = ThemePreset.Defaults[id].Accent;
            Assert.True(ContrastRatio(white, accent) < 3.0,
                $"a white ON thumb on {id}'s accent was the second defect; if it now clears 3:1 the "
                + "accent seed changed and these rules need re-deriving.");
        }
    }

    /// <summary>
    /// Mirrors <c>ThemeService.Lerp</c> so the toggle track is derived with the same maths the app uses,
    /// rather than an approximation of it. <c>ThemeStatusBrushTests</c> keeps the same mirror for the
    /// same reason; <c>ThemeService.Lerp</c> is private and is not worth widening for a test.
    /// </summary>
    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

}
