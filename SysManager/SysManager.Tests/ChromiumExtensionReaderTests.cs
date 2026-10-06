// SysManager · ChromiumExtensionReaderTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Text;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// One Chromium profile's extensions, read from a profile tree built in a temp folder the way Chrome, Edge, Brave,
/// Vivaldi and Opera lay theirs out (#1526): an <c>Extensions</c> folder with one subfolder per extension and per
/// version, and the profile's settings file. No real profile is read.
/// </summary>
public sealed class ChromiumExtensionReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SysManagerChromiumExt_" + Guid.NewGuid().ToString("N"));
    private readonly string _profile;

    private const string IdA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string IdB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    // Midday UTC, written the way Chromium writes it: microseconds since 1601, as a string.
    private static readonly DateTime Midday = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string MiddayMicroseconds = (Midday.ToFileTimeUtc() / 10).ToString(CultureInfo.InvariantCulture);

    public ChromiumExtensionReaderTests()
    {
        _profile = Path.Combine(_root, "Default");
        Directory.CreateDirectory(_profile);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort teardown */ }
    }

    private static string Manifest(string name = "Example", string version = "1.0", string extra = "") =>
        $$"""{ "manifest_version": 3, "name": "{{name}}", "version": "{{version}}"{{extra}} }""";

    /// <summary>Writes an extension's version folder with its manifest and any other files it carries.</summary>
    private string Install(string id, string version, string manifest, params (string Path, byte[] Bytes)[] files)
    {
        var dir = Path.Combine(_profile, "Extensions", id, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest);
        foreach (var (relative, bytes) in files)
        {
            var full = Path.Combine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
        return dir;
    }

    private void Settings(params (string Id, string Body)[] entries) => WriteSettings("Secure Preferences", entries);

    private void WriteSettings(string file, params (string Id, string Body)[] entries) =>
        File.WriteAllText(Path.Combine(_profile, file),
            """{ "extensions": { "settings": { """ + string.Join(", ", entries.Select(e => $"\"{e.Id}\": {e.Body}")) + " } } }");

    private (IReadOnlyList<BrowserExtension> Extensions, bool CouldNotRead) Read(CultureInfo? culture = null) =>
        ChromiumExtensionReader.Read(_profile, culture ?? English);

    private BrowserExtension Single(CultureInfo? culture = null)
    {
        var (extensions, couldNotRead) = Read(culture);
        Assert.False(couldNotRead);
        return Assert.Single(extensions);
    }

    // ── Which folders are extensions ────────────────────────────────────────

    [Fact]
    public void AnExtension_IsReadFromItsNewestVersionFolder_ComparedNumberByNumber()
    {
        Install(IdA, "9.1_0", Manifest(version: "9.1"));
        Install(IdA, "10.0_0", Manifest(version: "10.0"));

        Assert.Equal("10.0", Single().Version);
    }

    [Fact]
    public void ANewerVersionFolderWithoutAManifest_IsPassedOver()
    {
        // Mid-update, the browser can have made the new version's folder before writing its manifest.
        Install(IdA, "1.0_0", Manifest(version: "1.0"));
        Directory.CreateDirectory(Path.Combine(_profile, "Extensions", IdA, "2.0_0"));

        Assert.Equal("1.0", Single().Version);
    }

    [Fact]
    public void FoldersThatAreNotExtensions_AreLeftOut()
    {
        // Chromium's own working folder, an extension folder with no version, and a version with no manifest.
        var temp = Path.Combine(_profile, "Extensions", "Temp", "1.0_0");
        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(temp, "manifest.json"), Manifest());
        Directory.CreateDirectory(Path.Combine(_profile, "Extensions", IdA));
        Directory.CreateDirectory(Path.Combine(_profile, "Extensions", IdB, "1.0_0"));

        var (extensions, couldNotRead) = Read();

        Assert.Empty(extensions);
        Assert.False(couldNotRead);
    }

    [Fact]
    public void AProfileWithoutAnExtensionsFolder_HasNone_WhichIsNotAFailure()
    {
        var (extensions, couldNotRead) = Read();

        Assert.Empty(extensions);
        Assert.False(couldNotRead);
    }

    [Fact]
    public void AFileWhereTheExtensionsFolderShouldBe_CouldNotBeRead()
    {
        File.WriteAllText(Path.Combine(_profile, "Extensions"), "not a folder");

        var (extensions, couldNotRead) = Read();

        Assert.Empty(extensions);
        Assert.True(couldNotRead);
    }

    [Theory]
    [InlineData(IdA, true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaq", false)]
    [InlineData("aaaa", false)]
    [InlineData("Temp", false)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    public void OnlyThirtyTwoLettersAToP_NameAnExtension(string name, bool expected) =>
        Assert.Equal(expected, ChromiumExtensionReader.IsExtensionId(name));

    // ── Name, version and icon from the manifest ────────────────────────────

    [Fact]
    public void ATranslatedName_IsLookedUp_WithoutRegardToCase()
    {
        Install(IdA, "1.0_0", Manifest("__MSG_appName__", extra: """, "default_locale": "en" """),
            ("_locales/en/messages.json", Encoding.UTF8.GetBytes("""{ "AppName": { "message": "Video Helper" } }""")));

        Assert.Equal("Video Helper", Single().Name);
    }

    [Fact]
    public void TheUsersOwnLanguage_ComesBeforeTheExtensionsDefault()
    {
        Install(IdA, "1.0_0", Manifest("__MSG_name__", extra: """, "default_locale": "en" """),
            ("_locales/en/messages.json", Encoding.UTF8.GetBytes("""{ "name": { "message": "Video Helper" } }""")),
            ("_locales/ro/messages.json", Encoding.UTF8.GetBytes("""{ "name": { "message": "Ajutor video" } }""")));

        Assert.Equal("Ajutor video", Single(CultureInfo.GetCultureInfo("ro-RO")).Name);
        Assert.Equal("Video Helper", Single(English).Name);
    }

    [Fact]
    public void ANameThatCannotBeLookedUp_StillListsTheExtension()
    {
        Install(IdA, "1.0_0", Manifest("__MSG_missing__"));

        Assert.Equal(ChromiumExtensionReader.UnreadableName, Single().Name);
    }

    [Fact]
    public void TheVersionName_IsShownWhenThereIsOne()
    {
        Install(IdA, "1.2.3.4_0", Manifest(version: "1.2.3.4", extra: """, "version_name": "1.2 beta" """));

        Assert.Equal("1.2 beta", Single().Version);
    }

    [Fact]
    public void AManifestWithAByteOrderMarkCommentsAndATrailingComma_IsStillRead()
    {
        var dir = Install(IdA, "1.0_0", "{}");
        File.WriteAllBytes(Path.Combine(dir, "manifest.json"),
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""
                {
                  // Chromium allows comments in a manifest.
                  "name": "Commented", "version": "2.0",
                }
                """)]);

        Assert.Equal("Commented", Single().Name);
    }

    [Fact]
    public void TheIcon_IsTheSmallestAtLeast32Pixels()
    {
        Install(IdA, "1.0_0", Manifest(extra: """, "icons": { "16": "i16.png", "48": "i48.png", "128": "i128.png" } """),
            ("i16.png", [16]), ("i48.png", [48]), ("i128.png", [128]));

        Assert.Equal([48], Single().IconBytes);
    }

    [Fact]
    public void AnIconLargerThanItsBound_IsNotRead()
    {
        Install(IdA, "1.0_0", Manifest(extra: """, "icons": { "48": "i48.png" } """),
            ("i48.png", new byte[ExtensionFiles.MaxIconBytes + 1]));

        Assert.Null(Single().IconBytes);
    }

    [Fact]
    public void AnIconPathThatLeavesTheExtensionsFolder_IsNotRead()
    {
        File.WriteAllBytes(Path.Combine(_root, "outside.png"), [9]);
        Install(IdA, "1.0_0", Manifest(extra: """, "icons": { "48": "../../../../outside.png" } """));

        Assert.Null(Single().IconBytes);
    }

    [Fact]
    public void AnIconWithALeadingSlash_IsReadFromTheExtensionsOwnRoot()
    {
        Install(IdA, "1.0_0", Manifest(extra: """, "icons": { "48": "/images/i48.png" } """), ("images/i48.png", [48]));

        Assert.Equal([48], Single().IconBytes);
    }

    [Fact]
    public void WhatItAsksFor_IsReadFromItsManifest()
    {
        Install(IdA, "1.0_0", Manifest(extra: """
            , "permissions": ["tabs", "storage"],
              "host_permissions": ["https://docs.example.com/*"],
              "content_scripts": [ { "matches": ["<all_urls>"], "js": ["a.js"] } ],
              "chrome_settings_overrides": { "search_provider": { "name": "Other search" } },
              "chrome_url_overrides": { "newtab": "tab.html" }
            """));

        var extension = Single();

        Assert.True(extension.CanReadEverySite);
        Assert.Equal(
            ["Changes your search engine", "Replaces your new tab page", ExtensionPermissions.EverySite,
             "Can see your open tabs", "Other: storage"],
            extension.Permissions.Select(p => p.Text));
    }

    // ── What the settings add ───────────────────────────────────────────────

    [Fact]
    public void TheSettings_SayWhereItCameFrom_WhetherItIsOff_AndWhenItWasInstalled()
    {
        Install(IdA, "1.0_0", Manifest(extra: """, "update_url": "https://clients2.google.com/service/update2/crx" """));
        Settings((IdA, $$"""{ "location": 1, "from_webstore": true, "disable_reasons": [1], "first_install_time": "{{MiddayMicroseconds}}" }"""));

        var extension = Single();

        Assert.Equal(ExtensionOrigin.Store, extension.Origin);
        Assert.Equal("the Chrome Web Store", extension.Store);
        Assert.True(extension.IsOff);
        Assert.Equal(Midday, extension.InstalledOn);
    }

    [Fact]
    public void WithoutItsSettings_AnExtensionIsListedFromItsManifestAlone_ClaimingNothing()
    {
        Install(IdA, "1.0_0", Manifest());

        var extension = Single();

        Assert.Equal(ExtensionOrigin.Unknown, extension.Origin);
        Assert.False(extension.IsOff);
        Assert.Null(extension.InstalledOn);
        Assert.Equal("", extension.Store);
    }

    [Theory]
    [InlineData("""{ "protection": {} }""")]
    [InlineData("""{ "extensions": { "settings": {} } }""")]
    public void SettingsKeptInPreferences_AreUsed_WhenSecurePreferencesHasNone(string securePreferences)
    {
        Install(IdA, "1.0_0", Manifest());
        File.WriteAllText(Path.Combine(_profile, "Secure Preferences"), securePreferences);
        WriteSettings("Preferences", (IdA, """{ "location": 7 }"""));

        Assert.Equal(ExtensionOrigin.Organisation, Single().Origin);
    }

    [Theory]
    [InlineData("\"disable_reasons\": []", false)]
    [InlineData("\"disable_reasons\": [1]", true)]
    [InlineData("\"disable_reasons\": 0", false)]
    [InlineData("\"disable_reasons\": 4", true)]
    [InlineData("\"state\": 0", true)]
    [InlineData("\"state\": 1", false)]
    public void TurnedOff_IsReadEveryWayChromiumHasWrittenIt(string field, bool off)
    {
        Install(IdA, "1.0_0", Manifest());
        Settings((IdA, $"{{ \"location\": 1, {field} }}"));

        Assert.Equal(off, Single().IsOff);
    }

    [Theory]
    [InlineData(1, true, false, ExtensionOrigin.Store)]
    [InlineData(1, false, false, ExtensionOrigin.File)]
    [InlineData(2, false, false, ExtensionOrigin.AnotherProgram)]
    [InlineData(3, false, false, ExtensionOrigin.AnotherProgram)]
    [InlineData(6, true, false, ExtensionOrigin.AnotherProgram)]
    [InlineData(6, true, true, ExtensionOrigin.Browser)]
    [InlineData(7, false, false, ExtensionOrigin.Organisation)]
    [InlineData(9, false, false, ExtensionOrigin.Organisation)]
    [InlineData(5, false, false, ExtensionOrigin.Browser)]
    [InlineData(10, true, false, ExtensionOrigin.Browser)]
    [InlineData(4, false, false, ExtensionOrigin.Folder)]
    [InlineData(8, false, false, ExtensionOrigin.Folder)]
    [InlineData(99, false, false, ExtensionOrigin.Unknown)]
    [InlineData(null, true, false, ExtensionOrigin.Unknown)]
    public void EachLocationTheBrowserRecords_NamesWhereItCameFrom(int? location, bool fromWebstore, bool cameWithBrowser,
        ExtensionOrigin expected) =>
        Assert.Equal(expected, ChromiumExtensionReader.OriginOf(location, fromWebstore, cameWithBrowser));

    [Fact]
    public void AnExtensionLoadedFromAFolderElsewhere_IsFoundThroughItsSettings()
    {
        var unpacked = Path.Combine(_root, "my-extension");
        Directory.CreateDirectory(unpacked);
        File.WriteAllText(Path.Combine(unpacked, "manifest.json"), Manifest("Work in progress"));
        Settings(("cccccccccccccccccccccccccccccccc",
            $$"""{ "location": 4, "path": "{{unpacked.Replace("\\", "\\\\", StringComparison.Ordinal)}}" }"""));

        var extension = Single();

        Assert.Equal("Work in progress", extension.Name);
        Assert.Equal(ExtensionOrigin.Folder, extension.Origin);
    }

    [Fact]
    public void AnUnpackedEntryWhoseFolderIsGone_IsLeftOut()
    {
        Settings(("cccccccccccccccccccccccccccccccc", """{ "location": 4, "path": "C:\\\\nowhere\\\\gone" }"""));

        Assert.Empty(Read().Extensions);
    }

    [Theory]
    [InlineData("https://clients2.google.com/service/update2/crx", "the Chrome Web Store")]
    [InlineData("https://clients2.notgoogle.com/service/update2/crx", "an extension store")]
    [InlineData("https://edge.microsoft.com/extensionwebstorebase/v1/crx", "Microsoft Edge Add-ons")]
    [InlineData("https://extension-updates.opera.com/api/omaha/update/", "Opera add-ons")]
    [InlineData("https://updates.example.com/crx", "an extension store")]
    [InlineData(null, "an extension store")]
    public void TheStore_IsNamedByWhereUpdatesComeFrom(string? updateUrl, string expected) =>
        Assert.Equal(expected, ChromiumExtensionReader.StoreOf(updateUrl));

    // ── What cannot be read is still listed, or said ────────────────────────

    [Theory]
    [InlineData("\"location\": \"1\"")]
    [InlineData("\"location\": null")]
    [InlineData("\"state\": null")]
    [InlineData("\"state\": \"0\"")]
    [InlineData("\"disable_reasons\": \"1\"")]
    [InlineData("\"first_install_time\": 13300000000000000")]
    public void ASettingOfTheWrongKind_IsReadAsUnset_AndTheListCarriesOn(string field)
    {
        // Another program can write anything into these files, for an extension that has no folder too; one odd
        // value must not cost the whole look.
        Install(IdA, "1.0_0", Manifest());
        Settings((IdA, $"{{ {field} }}"), (IdB, $"{{ {field} }}"));

        var extension = Single();

        Assert.Equal(ExtensionOrigin.Unknown, extension.Origin);
        Assert.False(extension.IsOff);
        Assert.Null(extension.InstalledOn);
    }

    [Fact]
    public void ANameThatIsNotValidText_IsListedAsUnreadable_AndTheListCarriesOn()
    {
        var dir = Install(IdA, "1.0_0", "{}");
        File.WriteAllBytes(Path.Combine(dir, "manifest.json"),
            [.. Encoding.UTF8.GetBytes("{ \"name\": \""), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\", \"version\": \"1.0\" }")]);
        Install(IdB, "1.0_0", Manifest("Second"));

        var (extensions, couldNotRead) = Read();

        Assert.False(couldNotRead);
        Assert.Equal([ChromiumExtensionReader.UnreadableName, "Second"], extensions.Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ASettingsIdThatIsNotValidText_DoesNotStopTheList()
    {
        Install(IdA, "1.0_0", Manifest());
        File.WriteAllBytes(Path.Combine(_profile, "Secure Preferences"),
            [.. Encoding.UTF8.GetBytes("{ \"extensions\": { \"settings\": { \""), 0xC3, 0x28,
             .. Encoding.UTF8.GetBytes($"\": {{ \"location\": 6 }}, \"{IdA}\": {{ \"location\": 1 }} }} }} }}")]);

        Assert.Equal("Example", Single().Name);
    }

    [Fact]
    public void AManifestThatCannotBeRead_StillListsTheExtension_WithWhatTheSettingsSay()
    {
        Install(IdA, "1.0_0", "{ not json");
        Settings((IdA, """{ "location": 6 }"""));

        var extension = Single();

        Assert.Equal(ChromiumExtensionReader.UnreadableName, extension.Name);
        Assert.Equal("", extension.Version);
        Assert.Equal(ExtensionOrigin.AnotherProgram, extension.Origin);
    }

    [Fact]
    public void AManifestPastItsBound_StillListsTheExtension()
    {
        Install(IdA, "1.0_0", Manifest(extra: $", \"description\": \"{new string('x', ExtensionFiles.MaxManifestBytes)}\""));

        Assert.Equal(ChromiumExtensionReader.UnreadableName, Single().Name);
    }

    [Fact]
    public void AnExtensionFolderThatIsALink_IsListed_WithoutBeingFollowed()
    {
        // The browser follows the link; SysManager does not, but must not act as if the extension were not there.
        var elsewhere = Path.Combine(_root, "elsewhere", "1.0_0");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "manifest.json"), Manifest("Read through the link"));
        Directory.CreateDirectory(Path.Combine(_profile, "Extensions"));
        Symlinks.RequireJunction(Path.Combine(_profile, "Extensions", IdA), Path.Combine(_root, "elsewhere"));
        Settings((IdA, """{ "location": 6 }"""));

        var extension = Single();

        Assert.Equal(ChromiumExtensionReader.UnreadableName, extension.Name);
        Assert.Equal(ExtensionOrigin.AnotherProgram, extension.Origin);
    }

    [Theory]
    [InlineData("2.0_0", ChromiumExtensionReader.UnreadableName)]
    [InlineData("0.9_0", "Real")]
    public void AVersionFolderThatIsALink_IsNotFollowed_AndMakesTheExtensionUnreadableWhenItIsTheNewest(
        string linkedVersion, string expected)
    {
        Install(IdA, "1.0_0", Manifest("Real", "1.0"));
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "manifest.json"), Manifest("Read through the link", "9.9"));
        Symlinks.RequireJunction(Path.Combine(_profile, "Extensions", IdA, linkedVersion), elsewhere);

        Assert.Equal(expected, Single().Name);
    }

    [Fact]
    public void AnExtensionsFolderThatIsALink_CouldNotBeRead()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Symlinks.RequireJunction(Path.Combine(_profile, "Extensions"), elsewhere);

        Assert.True(Read().CouldNotRead);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnpackedFolderThatIsALink_OrNotOnALocalDrive_IsListed_WithoutBeingRead(bool link)
    {
        var real = Path.Combine(_root, "my-extension");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "manifest.json"), Manifest("Read through it"));
        string path;
        if (link)
        {
            path = Path.Combine(_root, "linked-extension");
            Symlinks.RequireJunction(path, real);
        }
        else
        {
            // The same folder, written the way a network share or a device path is: such a path is never opened.
            path = @"\\?\" + real;
        }
        Settings(("cccccccccccccccccccccccccccccccc",
            $$"""{ "location": 4, "path": "{{path.Replace("\\", "\\\\", StringComparison.Ordinal)}}" }"""));

        var extension = Single();

        Assert.Equal(ChromiumExtensionReader.UnreadableName, extension.Name);
        Assert.Equal(ExtensionOrigin.Folder, extension.Origin);
    }
}
