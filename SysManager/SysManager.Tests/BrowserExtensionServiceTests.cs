// SysManager · BrowserExtensionServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Every browser profile's extensions, found the way Browser Cleaner finds the profiles, in the order the list
/// shows them, and the command that opens a browser on its extensions page (#1526). Runs against temp
/// LOCALAPPDATA and APPDATA trees; the launcher is a recorder, so no browser is ever started.
/// </summary>
public sealed class BrowserExtensionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SysManagerExtService_" + Guid.NewGuid().ToString("N"));
    private readonly string _local;
    private readonly string _roaming;
    private readonly List<ProcessStartInfo> _started = [];

    public BrowserExtensionServiceTests()
    {
        _local = Path.Combine(_root, "Local");
        _roaming = Path.Combine(_root, "Roaming");
        Directory.CreateDirectory(_local);
        Directory.CreateDirectory(_roaming);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort teardown */ }
    }

    private BrowserExtensionService Service(bool elevated = false) => new(_local, _roaming, start =>
    {
        _started.Add(start);
        return true;
    }, () => elevated);

    private static void Chromium(string profileDir, string id, string name)
    {
        var version = Path.Combine(profileDir, "Extensions", id, "1.0_0");
        Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(version, "manifest.json"), $$"""{ "name": "{{name}}", "version": "1.0" }""");
    }

    private const string IdA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string IdB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task EveryBrowserAndProfile_IsListed_UnderTheNameTheCleanerGivesIt()
    {
        Chromium(Path.Combine(_local, @"Google\Chrome\User Data\Default"), IdA, "One");
        Chromium(Path.Combine(_local, @"Google\Chrome\User Data\Profile 1"), IdA, "Two");
        Chromium(Path.Combine(_roaming, @"Opera Software\Opera GX Stable"), IdA, "Three");
        var firefox = Path.Combine(_roaming, @"Mozilla\Firefox\Profiles\abcd1234.default-release");
        Directory.CreateDirectory(firefox);
        File.WriteAllText(Path.Combine(firefox, "extensions.json"), """
            { "addons": [ { "id": "f@x", "type": "extension", "version": "1", "location": "app-profile", "active": true,
                            "defaultLocale": { "name": "Four" } } ] }
            """);

        var profiles = await Service().ScanAsync();

        Assert.Equal<(string, string?, string)>(
            [("Google Chrome", null, "One"), ("Google Chrome — Profile 1", "Profile 1", "Two"), ("Opera GX", null, "Three"), ("Firefox", null, "Four")],
            profiles.Select(p => (p.Browser, p.ProfileDirectory, Assert.Single(p.Extensions).Name)));
        Assert.Equal(["chrome://extensions", "chrome://extensions", BrowserExtensionService.OperaPage, BrowserExtensionService.FirefoxPage],
            profiles.Select(p => p.Page));
        Assert.Equal<string?>(["chrome.exe", "chrome.exe", null, BrowserExtensionService.FirefoxExecutable], profiles.Select(p => p.Executable));
    }

    [Fact]
    public async Task AProfileWithNoExtensions_IsLeftOut_ButOneWhoseListCouldNotBeRead_IsKept()
    {
        Directory.CreateDirectory(Path.Combine(_local, @"Microsoft\Edge\User Data\Default"));
        var unreadable = Path.Combine(_local, @"Microsoft\Edge\User Data\Profile 2");
        Directory.CreateDirectory(unreadable);
        File.WriteAllText(Path.Combine(unreadable, "Extensions"), "not a folder");

        var profile = Assert.Single(await Service().ScanAsync());

        Assert.Equal("Microsoft Edge — Profile 2", profile.Browser);
        Assert.True(profile.CouldNotRead);
        Assert.Empty(profile.Extensions);
    }

    [Fact]
    public void TheListPutsWhatAnotherProgramAddedFirst_ThenWhatReadsEverySite_ThenTheRestByName()
    {
        static BrowserExtension E(string name, ExtensionOrigin origin = ExtensionOrigin.Store, bool everySite = false) =>
            new(name, "1", origin, false, null, "", [], everySite, null);

        var ordered = BrowserExtensionService.Ordered(
        [
            E("zeta"), E("alpha"), E("Reads all", everySite: true), E("Pushed", ExtensionOrigin.AnotherProgram),
            E("Beta", ExtensionOrigin.Organisation),
        ]);

        // "alpha" before "Beta": by name as she reads it, not by character code, which would put every capital first.
        Assert.Equal(["Pushed", "Reads all", "alpha", "Beta", "zeta"], ordered.Select(e => e.Name));
    }

    [Fact]
    public async Task AnExtensionAnotherProgramAdded_IsListedFirstInItsProfile()
    {
        var profileDir = Path.Combine(_local, @"Google\Chrome\User Data\Default");
        Chromium(profileDir, IdA, "Alpha");
        Chromium(profileDir, IdB, "Pushed in");
        File.WriteAllText(Path.Combine(profileDir, "Secure Preferences"),
            $$"""{ "extensions": { "settings": { "{{IdA}}": { "location": 1, "from_webstore": true }, "{{IdB}}": { "location": 6 } } } }""");

        var profile = Assert.Single(await Service().ScanAsync());

        Assert.Equal(["Pushed in", "Alpha"], profile.Extensions.Select(e => e.Name));
    }

    [Fact]
    public async Task AUserDataFolderThatIsALink_IsShownAsUnreadable_NotAsNone()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Chromium(Path.Combine(elsewhere, "Default"), IdA, "Behind the link");
        Directory.CreateDirectory(Path.Combine(_local, "Google", "Chrome"));
        Symlinks.RequireJunction(Path.Combine(_local, @"Google\Chrome\User Data"), elsewhere);

        var profile = Assert.Single(await Service().ScanAsync());

        Assert.Equal("Google Chrome", profile.Browser);
        Assert.True(profile.CouldNotRead);
        Assert.Empty(profile.Extensions);
    }

    [Fact]
    public async Task AProfileFolderThatIsALink_IsShownAsUnreadable_NotAsNone()
    {
        // Moving a browser's profile to another drive with a junction is a common tip; the list must say it could
        // not read the profile rather than that the browser has nothing.
        Chromium(Path.Combine(_local, @"Google\Chrome\User Data\Default"), IdB, "Real");
        var chrome = Path.Combine(_root, "chrome-elsewhere");
        Chromium(chrome, IdA, "Behind the link");
        Symlinks.RequireJunction(Path.Combine(_local, @"Google\Chrome\User Data\Profile 1"), chrome);
        var opera = Path.Combine(_root, "opera-elsewhere");
        Chromium(opera, IdA, "Behind the link");
        Directory.CreateDirectory(Path.Combine(_roaming, "Opera Software"));
        Symlinks.RequireJunction(Path.Combine(_roaming, @"Opera Software\Opera Stable"), opera);
        var firefox = Path.Combine(_root, "firefox-elsewhere");
        Directory.CreateDirectory(firefox);
        Directory.CreateDirectory(Path.Combine(_roaming, @"Mozilla\Firefox\Profiles"));
        Symlinks.RequireJunction(Path.Combine(_roaming, @"Mozilla\Firefox\Profiles\abcd1234.default-release"), firefox);

        var profiles = await Service().ScanAsync();

        Assert.Equal<(string, bool, int)>(
            [("Google Chrome", false, 1), ("Google Chrome — Profile 1", true, 0), ("Opera", true, 0), ("Firefox", true, 0)],
            profiles.Select(p => (p.Browser, p.CouldNotRead, p.Extensions.Count)));
    }

    [Fact]
    public async Task AFirefoxProfilesFolderThatIsALink_IsShownAsUnreadable_NotAsNone()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "abcd1234.default-release"));
        Directory.CreateDirectory(Path.Combine(_roaming, "Mozilla", "Firefox"));
        Symlinks.RequireJunction(Path.Combine(_roaming, @"Mozilla\Firefox\Profiles"), elsewhere);

        var profile = Assert.Single(await Service().ScanAsync());

        Assert.Equal("Firefox", profile.Browser);
        Assert.True(profile.CouldNotRead);
    }

    [Fact]
    public async Task AFirefoxProfileOtherThanTheDefault_IsNotStarted_SinceFirefoxWouldOpenAnother()
    {
        foreach (var folder in new[] { "abcd1234.default-release", "efgh5678.dev-edition" })
        {
            var dir = Path.Combine(_roaming, @"Mozilla\Firefox\Profiles", folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "extensions.json"), """
                { "addons": [ { "id": "f@x", "type": "extension", "version": "1", "location": "app-profile", "active": true,
                                "defaultLocale": { "name": "Four" } } ] }
                """);
        }

        var profiles = await Service().ScanAsync();

        Assert.Equal<(string, string?)>([("Firefox", BrowserExtensionService.FirefoxExecutable), ("Firefox — dev-edition", null)],
            profiles.Select(p => (p.Browser, p.Executable)));
    }

    [Theory]
    // Firefox 67 and later: each install names its default. Older Firefox marked one profile default; newer ones leave
    // that mark behind on another profile, as this PC's own file does, and the install's default wins.
    [InlineData("[Install4F96D1932A9F858E]\nDefault=Profiles/efgh5678.default-esr\nLocked=1\n\n[Profile0]\nPath=Profiles/abcd1234.default-release\nDefault=1\n", false, true)]
    [InlineData("[Profile0]\nName=default\nIsRelative=1\nPath=Profiles/efgh5678.default-esr\nDefault=1\n\n[Profile1]\nPath=Profiles/abcd1234.default-release\n", false, true)]
    [InlineData("[Install1]\nDefault=Profiles/abcd1234.default-release\n\n[Install2]\nDefault=Profiles/efgh5678.default-esr\n", false, false)]
    [InlineData("[General]\nStartWithLastProfile=1\n", false, false)]
    public async Task FirefoxIsStarted_OnlyForTheProfileItsOwnListSaysItOpens(string profilesIni, bool releaseStarted, bool esrStarted)
    {
        foreach (var folder in new[] { "abcd1234.default-release", "efgh5678.default-esr" })
        {
            var dir = Path.Combine(_roaming, @"Mozilla\Firefox\Profiles", folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "extensions.json"), """
                { "addons": [ { "id": "f@x", "type": "extension", "version": "1", "location": "app-profile", "active": true,
                                "defaultLocale": { "name": "Four" } } ] }
                """);
        }
        File.WriteAllText(Path.Combine(_roaming, @"Mozilla\Firefox\profiles.ini"), profilesIni.Replace("\n", "\r\n", StringComparison.Ordinal));

        var profiles = await Service().ScanAsync();

        Assert.Equal<(string, string?)>(
            [("Firefox", releaseStarted ? BrowserExtensionService.FirefoxExecutable : null),
             ("Firefox — default-esr", esrStarted ? BrowserExtensionService.FirefoxExecutable : null)],
            profiles.Select(p => (p.Browser, p.Executable)));
    }

    [Fact]
    public async Task ACancelledLook_StopsWithoutAnAnswer()
    {
        Chromium(Path.Combine(_local, @"Google\Chrome\User Data\Default"), IdA, "One");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ScanAsync(cts.Token));
    }

    // ── Opening a browser on its extensions page ────────────────────────────

    private static ExtensionProfile Profile(string? executable, string page, string? profileDirectory = null) =>
        new("Browser", "Browser", page, executable, profileDirectory, [], false);

    [Fact]
    public void ManageIn_StartsTheBrowserOnItsExtensionsPage()
    {
        Assert.Equal(ExtensionsPageOpening.Opened, Service().OpenExtensionsPage(Profile("chrome.exe", "chrome://extensions")));

        var start = Assert.Single(_started);
        Assert.Equal("chrome.exe", start.FileName);
        Assert.Equal("chrome://extensions", start.Arguments);
        Assert.True(start.UseShellExecute);
    }

    [Fact]
    public void ManageIn_ForASecondProfile_OpensThatProfile()
    {
        Service().OpenExtensionsPage(Profile("msedge.exe", "edge://extensions", "Profile 2"));

        Assert.Equal("--profile-directory=\"Profile 2\" edge://extensions", Assert.Single(_started).Arguments);
    }

    [Fact]
    public void ManageIn_ForFirefox_OpensItsAddOnsPage()
    {
        Service().OpenExtensionsPage(Profile(BrowserExtensionService.FirefoxExecutable, BrowserExtensionService.FirefoxPage));

        var start = Assert.Single(_started);
        Assert.Equal("firefox.exe", start.FileName);
        Assert.Equal("about:addons", start.Arguments);
    }

    [Fact]
    public void ManageIn_ForOpera_StartsNothing_SoTheUserIsToldWhereToGo()
    {
        Assert.Equal(ExtensionsPageOpening.NotOpened, Service().OpenExtensionsPage(Profile(null, BrowserExtensionService.OperaPage)));
        Assert.Empty(_started);
    }

    [Theory]
    [InlineData("cmd.exe", "chrome://extensions", null)]
    [InlineData("chrome.exe", "javascript:alert(1)", null)]
    [InlineData("chrome.exe", "edge://extensions", null)]
    [InlineData("chrome.exe", "chrome://extensions", "Profile 1\" --load-extension=\"C:\\x")]
    [InlineData("chrome.exe", "chrome://extensions", "System Profile")]
    [InlineData("firefox.exe", "about:addons", "Profile 1")]
    public void ManageIn_RefusesAnythingButAKnownBrowserPageAndARealProfileFolder(string executable, string page, string? profile)
    {
        Assert.Equal(ExtensionsPageOpening.NotOpened, Service().OpenExtensionsPage(Profile(executable, page, profile)));
        Assert.Empty(_started);
    }

    [Fact]
    public void WhileSysManagerRunsAsAdministrator_NoBrowserIsStarted()
    {
        // It would run as administrator too, and whatever chrome.exe resolves to is the user's own setting.
        Assert.Equal(ExtensionsPageOpening.NotWhileElevated,
            Service(elevated: true).OpenExtensionsPage(Profile("chrome.exe", "chrome://extensions")));
        Assert.Empty(_started);
    }

    [Fact]
    public void ABrowserThatCannotBeStartedAnyway_IsNotSaidToBeHeldBackByElevation()
    {
        Assert.Equal(ExtensionsPageOpening.NotOpened,
            Service(elevated: true).OpenExtensionsPage(Profile(null, BrowserExtensionService.OperaPage)));
        Assert.Empty(_started);
    }

    [Fact]
    public void ABrowserThatCannotBeStarted_IsReportedAsNotOpened()
    {
        var service = new BrowserExtensionService(_local, _roaming, _ => false, () => false);

        Assert.Equal(ExtensionsPageOpening.NotOpened, service.OpenExtensionsPage(Profile("chrome.exe", "chrome://extensions")));
    }
}
