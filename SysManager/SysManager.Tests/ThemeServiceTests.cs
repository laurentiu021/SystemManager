// SysManager · ThemeServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using System.Windows.Media;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ThemeService"/> persistence, made possible by the <c>configDir</c> seam added
/// for #1741. Before it, <c>SettingsPath</c> was a <c>static readonly</c> built from
/// <see cref="Environment.SpecialFolder.ApplicationData"/> — impossible to redirect, because that API
/// ignores the <c>APPDATA</c> environment variable — so any test that constructed the service and saved
/// would overwrite the developer's real <c>theme.json</c>. That is the exact data loss that hit
/// <c>SpeedTestHistoryService</c> (#1734).
/// <para>These run headless: <see cref="ThemeService.Apply"/> no-ops without an
/// <c>Application.Current</c>, so <c>Load</c>/<c>SetPreset</c> can run in the test host. They assert the
/// seam genuinely redirects the file AND that the real profile path is never touched.</para>
/// </summary>
public class ThemeServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "smtest_theme_" + Guid.NewGuid().ToString("N"));

    public ThemeServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a run */ }
        GC.SuppressFinalize(this);
    }

    private string ThemeFile => Path.Combine(_dir, "theme.json");

    [Fact]
    public void SetPreset_WritesToTheConfiguredDirectory_NotTheRealProfile()
    {
        var realPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SysManager", "theme.json");
        var existedBefore = File.Exists(realPath);
        var contentBefore = existedBefore ? File.ReadAllText(realPath) : null;

        var svc = new ThemeService(_dir);
        svc.SetPreset("deep-ocean");

        // The choice landed in the temp file…
        Assert.True(File.Exists(ThemeFile), "the theme was not written to the configured directory");
        Assert.Contains("deep-ocean", File.ReadAllText(ThemeFile));

        // …and the developer's real theme.json is byte-for-byte what it was (untouched, not created).
        Assert.Equal(existedBefore, File.Exists(realPath));
        if (existedBefore) Assert.Equal(contentBefore, File.ReadAllText(realPath));
    }

    [Fact]
    public void Initialize_ReadsBackWhatWasSaved()
    {
        // A first instance persists a choice…
        new ThemeService(_dir).SetPreset("dark-forest");

        // …and a second, pointed at the same directory, loads it on Initialize.
        var reloaded = new ThemeService(_dir);
        reloaded.Initialize();

        Assert.Equal("dark-forest", reloaded.CurrentPresetId);
    }

    [Fact]
    public void Initialize_WithNoFile_KeepsTheDefaultPreset()
    {
        var svc = new ThemeService(_dir);   // empty temp dir, no theme.json
        svc.Initialize();

        Assert.Equal("midnight-indigo", svc.CurrentPresetId);
        Assert.False(File.Exists(ThemeFile), "Initialize must not create a file when none exists");
    }

    [Fact]
    public void SetShade_PersistedValue_SurvivesAReload()
    {
        var svc = new ThemeService(_dir);
        svc.SetPreset("deep-ocean");
        // SetShade debounces its save behind a DispatcherTimer, which never ticks headless; SetPreset
        // saves immediately, so persist the shade by re-selecting the preset after moving it.
        svc.SetShade(0.8);
        svc.SetPreset("deep-ocean");

        var reloaded = new ThemeService(_dir);
        reloaded.Initialize();

        Assert.Equal(0.8, reloaded.ShadePosition, precision: 3);
    }

    /// <summary>
    /// Saving a custom theme and loading it back must be a fixed point: the file that comes out of a reload
    /// has to be byte-identical to the one that went in.
    /// </summary>
    /// <remarks>
    /// <c>CurrentTheme</c> is a pure function of <c>(_baseTheme, ShadePosition)</c>. <c>Save</c> persisted
    /// <c>CurrentTheme</c> — the derived value — and <c>Load</c>'s custom branch hands those four colours back
    /// to <c>SetCustom</c> as the new BASE. So every launch derived from an already-derived theme and a custom
    /// theme degraded a little each time, silently, with the user never touching anything.
    /// <para>Two independent drift channels, one per row here. At a non-default slider position the shade
    /// offset re-applies to already-shifted surfaces. At the DEFAULT position the offset is zero, but
    /// <c>Legible</c> still walks <c>TextPrimary</c> in 2% steps whenever it misses 7:1 against the
    /// background — and the walked value was what got saved, so the text marched on every restart with the
    /// slider untouched. A test at only one position would miss whichever channel it did not sit on.</para>
    /// <para>Asserting the FILE rather than a property is deliberate: the file is what survives a restart, and
    /// comparing generation 2 with generation 3 alone would pass even if generation 1 to 2 had already
    /// shifted — a drift that stops after one step is still a drift.</para>
    /// </remarks>
    [Theory]
    [InlineData(0.8, "#FF101418", "#FFF1F3F7", "off-centre slider: the shade offset re-shifts the surfaces")]
    [InlineData(0.5, "#FF202020", "#FF606060", "default slider: Legible still walks a text colour under 7:1")]
    public void ACustomTheme_SurvivesAReloadUnchanged(
        double shade, string background, string text, string channel)
    {
        var first = new ThemeService(_dir);
        first.SetShade(shade);
        // SetCustom saves immediately, which also persists the shade SetShade only debounced.
        first.SetCustom(
            (Color)ColorConverter.ConvertFromString("#FF6366F1")!,
            (Color)ColorConverter.ConvertFromString(background)!,
            (Color)ColorConverter.ConvertFromString("#FF181C22")!,
            (Color)ColorConverter.ConvertFromString(text)!);

        var generation1 = File.ReadAllText(ThemeFile);

        var second = new ThemeService(_dir);
        second.Initialize();
        var generation2 = File.ReadAllText(ThemeFile);

        var third = new ThemeService(_dir);
        third.Initialize();
        var generation3 = File.ReadAllText(ThemeFile);

        Assert.Equal(generation1, generation2);
        Assert.Equal(generation2, generation3);
        Assert.Equal("custom", third.CurrentPresetId);
        Assert.True(generation1.Contains(text, StringComparison.OrdinalIgnoreCase),
            $"{channel}: the saved file should hold the colour the user typed ({text}), not a value derived "
            + $"from it. It holds:\n{generation1}");
    }
}
