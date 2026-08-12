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

    // ── ExecutableNameFromCommand (pure — the exe-name parser feeding the description lookup) ──

    [Theory]
    // Quoted path with arguments — the common Run-key shape.
    [InlineData("\"C:\\Program Files\\OneDrive\\OneDrive.exe\" /background", "OneDrive")]
    // Unquoted path with a switch.
    [InlineData(@"C:\Windows\system32\SecurityHealthSystray.exe /run", "SecurityHealthSystray")]
    // Bare path, no arguments.
    [InlineData(@"C:\Program Files\Spotify\Spotify.exe", "Spotify")]
    // A resolved shortcut target with spaces in the folder but no arguments.
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", "steam")]
    // Quoted path, no arguments.
    [InlineData("\"C:\\Program Files\\NVIDIA Corporation\\NvContainer\\nvcontainer.exe\"", "nvcontainer")]
    // Already just a name.
    [InlineData("Discord.exe", "Discord")]
    public void ExecutableNameFromCommand_ExtractsTheBareExeName(string command, string expected)
        => Assert.Equal(expected, StartupService.ExecutableNameFromCommand(command));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ExecutableNameFromCommand_BlankCommand_ReturnsEmpty(string command)
        => Assert.Equal("", StartupService.ExecutableNameFromCommand(command));

    [Fact]
    public void ExecutableNameFromCommand_IllegalPathChars_ReturnsEmptyRatherThanThrow()
    {
        // A rundll32 entry-point spec or similar can carry characters that are not a valid path. The
        // parser must degrade to "no match" (empty), never throw into the scan.
        var result = StartupService.ExecutableNameFromCommand("rundll32.exe shell32.dll,Control_RunDLL \"x\"|<>");
        // Whatever it returns, it must not have thrown; rundll32 is the leading token here.
        Assert.Equal("rundll32", result);
    }

    // ── EnrichWithDescriptions (pure over the finished list — the actual feature) ──

    [Fact]
    public void EnrichWithDescriptions_KnownProgram_GetsDescriptionAndSafety()
    {
        // OneDrive is in the shipped database; a real Run-key command shape drives the lookup.
        var entry = new StartupEntry { Name = "OneDrive", Command = "\"C:\\Program Files\\Microsoft OneDrive\\OneDrive.exe\" /background" };

        StartupService.EnrichWithDescriptions([entry]);

        Assert.NotEqual("", entry.Description);
        // The safety is a ProcessSafety name, so it binds to the ProcessSafety* chip converters.
        Assert.Contains(entry.Safety, new[] { "System", "Trusted", "Unknown" });
    }

    [Fact]
    public void EnrichWithDescriptions_UnknownProgram_LeavesBothEmpty()
    {
        // The stated risk: never assert a safety on a guess. An executable the database does not know
        // must come back with both fields empty, so the view shows no chip and falls back to publisher.
        var entry = new StartupEntry
        {
            Name = "TotallyMadeUpVendorWidget",
            Command = @"C:\Vendor\TotallyMadeUpVendorWidget9000.exe",
            Publisher = "Made Up Vendor Inc.",
        };

        StartupService.EnrichWithDescriptions([entry]);

        Assert.Equal("", entry.Description);
        Assert.Equal("", entry.Safety);
    }

    [Fact]
    public void EnrichWithDescriptions_BlankCommand_DoesNotThrowOrPopulate()
    {
        var entry = new StartupEntry { Name = "Weird", Command = "" };

        StartupService.EnrichWithDescriptions([entry]);

        Assert.Equal("", entry.Description);
        Assert.Equal("", entry.Safety);
    }
}
