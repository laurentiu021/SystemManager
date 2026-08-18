// SysManager · ThemeServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
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
}
