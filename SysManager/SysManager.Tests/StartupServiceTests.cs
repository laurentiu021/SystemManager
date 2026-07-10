// SysManager · StartupServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="StartupService"/>. Verifies that the scanner
/// returns entries from registry, startup folders, and Task Scheduler
/// without crashing on any machine configuration.
/// </summary>
public class StartupServiceTests
{
    [Fact]
    public async Task ScanAsync_ReturnsNonNullList()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ScanAsync_EntriesHaveNonEmptyNames()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        foreach (var entry in result)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name),
                $"Entry with empty name found at location: {entry.Location}");
        }
    }

    [Fact]
    public async Task ScanAsync_EntriesHaveNonEmptyCommand()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        foreach (var entry in result)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Command),
                $"Entry '{entry.Name}' has empty command");
        }
    }

    [Fact]
    public async Task ScanAsync_EntriesHaveValidSource()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        foreach (var entry in result)
        {
            Assert.True(Enum.IsDefined(typeof(StartupSource), entry.Source),
                $"Entry '{entry.Name}' has invalid source: {entry.Source}");
        }
    }

    [Fact]
    public async Task ScanAsync_EntriesHaveLocation()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        foreach (var entry in result)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Location),
                $"Entry '{entry.Name}' has empty location");
        }
    }

    [Fact]
    public async Task ScanAsync_NoDuplicateNamesWithinSameSource()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        // Entries from different sources (registry vs folder vs scheduler)
        // may legitimately share a name. Within the same source, entries
        // from Run and RunOnce may also share a name (e.g. "desktop").
        // We check for exact (name + source + location) triples.
        var dupes = result
            .GroupBy(e => (e.Name.ToLowerInvariant(), e.Source, e.Location.ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Item1} ({g.Key.Source}, {g.Key.Item3})")
            .ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public async Task ScanAsync_StatusTextIsSet()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        foreach (var entry in result)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.StatusText),
                $"Entry '{entry.Name}' has empty StatusText");
        }
    }

    [Fact]
    public async Task ScanAsync_IsEnabledIsBoolean()
    {
        var svc = new StartupService();
        var result = await svc.ScanAsync();
        // Just verify no exceptions — IsEnabled is always bool by type,
        // but we want to ensure ApplyApprovedState doesn't corrupt it.
        foreach (var entry in result)
        {
            _ = entry.IsEnabled; // should not throw
        }
    }

    // ── BuildStartupFolderEntry (pure — the StartupApproved key-name toggle bug) ──

    [Theory]
    [InlineData(@"C:\Users\aunt\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\Spotify.lnk", "Spotify", "Spotify.lnk")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\Backup Tool.exe", "Backup Tool", "Backup Tool.exe")]
    public void BuildStartupFolderEntry_KeepsExtensionInValueName_DropsItInName(
        string file, string expectedName, string expectedValueName)
    {
        // Regression: the StartupApproved\StartupFolder registry key is keyed by the file's FULL
        // name (with extension). Keying by the extension-stripped name (the old behavior) made a
        // disabled item read back as enabled, and made "disable" write its blob under a name
        // Windows ignores — so the program kept launching. ValueName must retain the extension.
        var entry = StartupService.BuildStartupFolderEntry(file, command: file, locationLabel: "User Startup Folder");

        Assert.Equal(expectedName, entry.Name);           // display: extension stripped
        Assert.Equal(expectedValueName, entry.ValueName);  // StartupApproved key: full filename
        Assert.Equal(StartupSource.StartupFolder, entry.Source);
    }

    [Fact]
    public void BuildStartupFolderEntry_NameAndValueName_DoNotCollapse()
    {
        // Name (display) and ValueName (registry key) must stay distinct for an extensioned file,
        // so a shortcut and a same-stem executable cannot collide on the approved-state key.
        var entry = StartupService.BuildStartupFolderEntry(
            @"C:\Users\aunt\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\OneDrive.lnk",
            command: @"C:\Program Files\OneDrive\OneDrive.exe",
            locationLabel: "User Startup Folder");

        Assert.NotEqual(entry.Name, entry.ValueName);
        Assert.Equal("OneDrive", entry.Name);
        Assert.Equal("OneDrive.lnk", entry.ValueName);
    }

    // ── Common (all-users) vs per-user startup folder source (P2 #38) ──

    [Fact]
    public void BuildStartupFolderEntry_Common_TaggedCommonStartupFolder()
    {
        // Regression (P2 #38): all-users folder items store their enabled/disabled state under
        // HKLM, not HKCU. They must carry a distinct source so ApplyApprovedState/SetEnabledAsync
        // target the right hive — otherwise a disable is written to HKCU (where Windows never
        // looks) and silently does nothing while the UI claims "Disabled".
        var entry = StartupService.BuildStartupFolderEntry(
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\Backup Tool.exe",
            command: @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\Backup Tool.exe",
            locationLabel: "Common Startup Folder",
            isCommon: true);

        Assert.Equal(StartupSource.CommonStartupFolder, entry.Source);
        Assert.Equal("Backup Tool.exe", entry.ValueName);
    }

    [Fact]
    public void BuildStartupFolderEntry_PerUser_TaggedStartupFolder()
    {
        var entry = StartupService.BuildStartupFolderEntry(
            @"C:\Users\aunt\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\Spotify.lnk",
            command: @"C:\Users\aunt\AppData\Roaming\Spotify\Spotify.exe",
            locationLabel: "User Startup Folder",
            isCommon: false);

        Assert.Equal(StartupSource.StartupFolder, entry.Source);
    }
}
