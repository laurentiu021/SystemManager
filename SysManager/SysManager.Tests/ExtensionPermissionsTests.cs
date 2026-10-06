// SysManager · ExtensionPermissionsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// What an extension asks for, in the plain words Browser Cleaner's Extensions view shows (#1526): in a fixed
/// order, amber for what changes what the user sees or reaches every website, and nothing dropped.
/// </summary>
public class ExtensionPermissionsTests
{
    private static (IReadOnlyList<ExtensionPermission> Lines, bool EverySite) Describe(
        string[]? permissions = null, string[]? sites = null, bool search = false, bool home = false,
        bool newTab = false, bool startup = false) =>
        ExtensionPermissions.Describe(new(permissions ?? [], sites ?? [], search, home, newTab, startup));

    private static string[] Texts(IReadOnlyList<ExtensionPermission> lines) => [.. lines.Select(l => l.Text)];

    [Fact]
    public void EveryCuratedLine_ComesInItsFixedOrder_AndTheRestByName()
    {
        var (lines, everySite) = Describe(
            permissions: ["storage", "downloads", "nativeMessaging", "debugger", "webRequest", "cookies", "tabs", "history"],
            sites: ["<all_urls>"], search: true, home: true, newTab: true, startup: true);

        Assert.True(everySite);
        Assert.Equal(
        [
            "Changes your search engine",
            "Replaces your home page and new tab page",
            "Changes the pages that open when the browser starts",
            ExtensionPermissions.EverySite,
            "Can read your browsing history",
            "Can see your open tabs",
            "Can read your cookies",
            "Can block or change web traffic",
            "Can control the browser like a developer tool",
            "Can talk to a program on this PC",
            "Can manage your downloads",
            "Other: storage",
        ], Texts(lines));
    }

    [Fact]
    public void WhatChangesWhatSheSees_ReachesEverySite_OrControlsTheBrowser_IsAmber_AndTheRestIsNot()
    {
        var (lines, _) = Describe(permissions: ["debugger", "tabs", "storage"], sites: ["*://*/*"], search: true, newTab: true);

        Assert.Equal(
            ["Changes your search engine", "Replaces your new tab page", ExtensionPermissions.EverySite,
             "Can control the browser like a developer tool"],
            Texts([.. lines.Where(l => l.IsWarning)]));
        Assert.Equal(["Can see your open tabs", "Other: storage"], Texts([.. lines.Where(l => !l.IsWarning)]));
    }

    [Theory]
    [InlineData(true, false, "Replaces your home page")]
    [InlineData(false, true, "Replaces your new tab page")]
    [InlineData(true, true, "Replaces your home page and new tab page")]
    public void AReplacedPage_IsNamed(bool home, bool newTab, string expected) =>
        Assert.Equal([expected], Texts(Describe(home: home, newTab: newTab).Lines));

    [Theory]
    [InlineData("<all_urls>")]
    [InlineData("*://*/*")]
    [InlineData("http://*/*")]
    [InlineData("https://*/*")]
    [InlineData("*://*/")]
    public void EachEverySitePattern_ReadsAsEveryWebsite(string pattern)
    {
        var (lines, everySite) = Describe(sites: [pattern]);

        Assert.True(everySite);
        Assert.Equal([ExtensionPermissions.EverySite], Texts(lines));
    }

    [Fact]
    public void ASiteListedWithThePermissions_AsOlderExtensionsDo_IsReadAsASite_NotAsOther()
    {
        var (lines, everySite) = Describe(permissions: ["https://*/*", "storage"]);

        Assert.True(everySite);
        Assert.Equal([ExtensionPermissions.EverySite, "Other: storage"], Texts(lines));
    }

    [Fact]
    public void NamedSites_AreListed_WithoutTheirWildcardsOrPorts_AndOnlyOnce()
    {
        var (lines, everySite) = Describe(sites:
            ["https://docs.google.com/*", "*://*.YouTube.com/*", "http://localhost:8080/*", "*://*.youtube.com/watch*"]);

        Assert.False(everySite);
        Assert.Equal(["Can read and change data on docs.google.com, youtube.com, localhost"], Texts(lines));
    }

    [Theory]
    [InlineData(4, "Can read and change data on a.com, b.com, c.com and 1 more site")]
    [InlineData(5, "Can read and change data on a.com, b.com, c.com and 2 more sites")]
    [InlineData(3, "Can read and change data on a.com, b.com, c.com")]
    public void PastThreeSites_TheRestAreCounted(int count, string expected)
    {
        string[] sites = [.. "abcde".Take(count).Select(c => $"https://{c}.com/*")];

        Assert.Equal([expected], Texts(Describe(sites: sites).Lines));
    }

    [Fact]
    public void PatternsThatAreNotWebPages_NameNoSite()
    {
        var (lines, everySite) = Describe(sites: ["file:///*", "chrome-extension://abc/*"]);

        Assert.False(everySite);
        Assert.Empty(lines);
    }

    [Fact]
    public void APermissionTheListDoesNotKnow_IsShownByItsName_NotDropped()
    {
        var (lines, _) = Describe(permissions: ["somethingNew", "alarms", "somethingNew"]);

        Assert.Equal(["Other: somethingNew", "Other: alarms"], Texts(lines));
    }

    [Theory]
    [InlineData("webRequestBlocking")]
    [InlineData("declarativeNetRequest")]
    [InlineData("declarativeNetRequestWithHostAccess")]
    public void EachWayOfBlockingTraffic_ReadsAsOneLine(string permission) =>
        Assert.Equal(["Can block or change web traffic"], Texts(Describe(permissions: [permission, "webRequest"]).Lines));

    [Fact]
    public void AnExtensionAskingForNothing_HasNoLines()
    {
        var (lines, everySite) = Describe();

        Assert.Empty(lines);
        Assert.False(everySite);
    }

    [Theory]
    [InlineData("https://Docs.Google.com:443/path", "docs.google.com")]
    [InlineData("*://*.example.org/*", "example.org")]
    [InlineData("wss://chat.example.org/*", "chat.example.org")]
    [InlineData("file:///C:/*", "")]
    [InlineData("<all_urls>", "")]
    public void HostOf_NamesTheSite(string pattern, string expected) =>
        Assert.Equal(expected, ExtensionPermissions.HostOf(pattern));
}
