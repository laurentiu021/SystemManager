// SysManager · FirefoxExtensionReaderTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.IO.Compression;
using System.Text;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// One Firefox profile's extensions, read from a profile built in a temp folder the way Firefox lays one out
/// (#1526): <c>extensions.json</c>, and each extension's <c>.xpi</c> package for its icon and the pages it replaces.
/// No real profile is read.
/// </summary>
public sealed class FirefoxExtensionReaderTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "SysManagerFirefoxExt_" + Guid.NewGuid().ToString("N"));

    // Midday UTC, written the way Firefox writes it: milliseconds since 1970.
    private static readonly DateTime Midday = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long MiddayMilliseconds = new DateTimeOffset(Midday).ToUnixTimeMilliseconds();

    public FirefoxExtensionReaderTests() => Directory.CreateDirectory(_profile);

    public void Dispose()
    {
        try { Directory.Delete(_profile, recursive: true); }
        catch (IOException) { /* best-effort teardown */ }
    }

    private static string Addon(string name = "Video Speed Controller", string type = "extension", string location = "app-profile",
        string extra = "") =>
        $$"""
          { "id": "{{name}}@example", "type": "{{type}}", "version": "0.9.4", "location": "{{location}}",
            "active": true, "userDisabled": false, "defaultLocale": { "name": "{{name}}" }{{extra}} }
          """;

    private void Write(params string[] addons) =>
        File.WriteAllText(Path.Combine(_profile, "extensions.json"),
            $$"""{ "schemaVersion": 37, "addons": [ {{string.Join(", ", addons)}} ] }""");

    private (IReadOnlyList<BrowserExtension> Extensions, bool CouldNotRead) Read() => FirefoxExtensionReader.Read(_profile);

    private BrowserExtension Single()
    {
        var (extensions, couldNotRead) = Read();
        Assert.False(couldNotRead);
        return Assert.Single(extensions);
    }

    /// <summary>Builds an extension package with a manifest and, when given, an icon entry.</summary>
    private string Package(string manifest, string? iconEntry = null, byte[]? icon = null)
    {
        var path = Path.Combine(_profile, "extensions", "video@example.xpi");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), Encoding.UTF8))
                writer.Write(manifest);
            if (iconEntry is not null && icon is not null)
            {
                using var stream = zip.CreateEntry(iconEntry).Open();
                stream.Write(icon);
            }
        }
        return path;
    }

    private static string Json(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    [Fact]
    public void AProfileWithoutTheList_HasNoExtensions_WhichIsNotAFailure()
    {
        var (extensions, couldNotRead) = Read();

        Assert.Empty(extensions);
        Assert.False(couldNotRead);
    }

    [Fact]
    public void AListThatIsNotJson_CouldNotBeRead()
    {
        File.WriteAllText(Path.Combine(_profile, "extensions.json"), "{ not json");

        var (extensions, couldNotRead) = Read();

        Assert.Empty(extensions);
        Assert.True(couldNotRead);
    }

    [Fact]
    public void AnExtension_IsListedWithItsNameVersionInstallDateAndWhatTheUserGrantedIt()
    {
        Write(Addon(extra: $$"""
            , "installDate": {{MiddayMilliseconds}},
              "userPermissions": { "permissions": ["tabs", "storage"], "origins": ["*://*.youtube.com/*"] }
            """));

        var extension = Single();

        Assert.Equal("Video Speed Controller", extension.Name);
        Assert.Equal("0.9.4", extension.Version);
        Assert.Equal(Midday, extension.InstalledOn);
        Assert.False(extension.IsOff);
        Assert.False(extension.CanReadEverySite);
        Assert.Equal(["Can read and change data on youtube.com", "Can see your open tabs", "Other: storage"],
            extension.Permissions.Select(p => p.Text));
    }

    [Fact]
    public void ThemesDictionariesLanguagePacks_AndAddOnsFirefoxHides_AreLeftOut()
    {
        Write(
            Addon("Dark", type: "theme"),
            Addon("English Dictionary", type: "dictionary"),
            Addon("Language Pack", type: "locale"),
            Addon("Built In", location: "app-builtin-addons", extra: """, "hidden": true"""),
            Addon("Shadowed", extra: """, "visible": false"""),
            Addon("Kept"));

        Assert.Equal("Kept", Single().Name);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void AnExtensionThatIsNotActive_OrTheUserTurnedOff_IsOff(bool active, bool userDisabled, bool off)
    {
        Write($$"""
            { "id": "x@example", "type": "extension", "version": "1", "location": "app-profile",
              "active": {{(active ? "true" : "false")}}, "userDisabled": {{(userDisabled ? "true" : "false")}},
              "defaultLocale": { "name": "X" } }
            """);

        Assert.Equal(off, Single().IsOff);
    }

    [Theory]
    [InlineData("app-profile", false, "amo", null, ExtensionOrigin.Store)]
    [InlineData("app-profile", false, null, "https://addons.mozilla.org/firefox/downloads/file/1/x.xpi", ExtensionOrigin.Store)]
    [InlineData("app-profile", false, "file-url", "file:///C:/Downloads/x.xpi", ExtensionOrigin.File)]
    [InlineData("app-profile", false, null, "https://notaddons.mozilla.org/x.xpi", ExtensionOrigin.File)]
    [InlineData("app-profile", true, "sideload", null, ExtensionOrigin.AnotherProgram)]
    [InlineData("winreg-app-user", false, null, null, ExtensionOrigin.AnotherProgram)]
    [InlineData("app-system-share", false, null, null, ExtensionOrigin.AnotherProgram)]
    [InlineData("app-profile", false, "enterprise-policy", null, ExtensionOrigin.Organisation)]
    [InlineData("app-builtin-addons", false, "app-builtin", null, ExtensionOrigin.Browser)]
    [InlineData("app-system-defaults", false, null, null, ExtensionOrigin.Browser)]
    [InlineData("app-temporary", false, "temporary-addon", null, ExtensionOrigin.Folder)]
    [InlineData("somewhere-new", false, null, null, ExtensionOrigin.Unknown)]
    public void EachWayFirefoxRecordsAnInstall_NamesWhereItCameFrom(string location, bool foreign, string? source, string? sourceUri,
        ExtensionOrigin expected) =>
        Assert.Equal(expected, FirefoxExtensionReader.OriginOf(location, foreign, source, sourceUri));

    [Fact]
    public void AnExtensionFromTheAddOnsSite_SaysSo()
    {
        Write(Addon(extra: """, "installTelemetryInfo": { "source": "amo" }"""));

        var extension = Single();

        Assert.Equal(ExtensionOrigin.Store, extension.Origin);
        Assert.Equal("Firefox Add-ons", extension.Store);
    }

    [Fact]
    public void TheIcon_AndThePagesItReplaces_ComeFromItsPackage()
    {
        var package = Package("""
            { "manifest_version": 2, "name": "Search Pro",
              "chrome_settings_overrides": { "search_provider": { "name": "Search Pro" }, "homepage": "https://example.com" } }
            """, "icons/icon-48.png", [7, 7, 7]);
        Write(Addon(extra: $$"""
            , "path": "{{Json(package)}}", "icons": { "16": "icons/icon-16.png", "48": "icons/icon-48.png" }
            """));

        var extension = Single();

        Assert.Equal([7, 7, 7], extension.IconBytes);
        Assert.Equal(["Changes your search engine", "Replaces your home page"], extension.Permissions.Select(p => p.Text));
    }

    [Fact]
    public void AnIconLargerThanItsBound_IsNotRead()
    {
        var package = Package("""{ "manifest_version": 2, "name": "Big" }""", "icon.png", new byte[ExtensionFiles.MaxIconBytes + 1]);
        Write(Addon(extra: $$""", "path": "{{Json(package)}}", "icons": { "48": "icon.png" } """));

        Assert.Null(Single().IconBytes);
    }

    [Fact]
    public void AnExtensionWhosePackageIsMissing_IsStillListed()
    {
        Write(Addon(extra: $$""", "path": "{{Json(Path.Combine(_profile, "gone.xpi"))}}" """));

        var extension = Single();

        Assert.Null(extension.IconBytes);
        Assert.Equal("Video Speed Controller", extension.Name);
    }

    [Fact]
    public void APackageThatIsNotAZip_IsStillListed()
    {
        var path = Path.Combine(_profile, "broken.xpi");
        File.WriteAllText(path, "not a zip");
        Write(Addon(extra: $$""", "path": "{{Json(path)}}", "icons": { "48": "i.png" } """));

        Assert.Null(Single().IconBytes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"1759147200000\"")]
    public void AnInstallDateOfTheWrongKind_IsNoDate_AndTheListCarriesOn(string value)
    {
        Write(Addon(extra: $", \"installDate\": {value}"), Addon("Second"));

        var (extensions, couldNotRead) = Read();

        Assert.False(couldNotRead);
        // The odd date is the only thing lost: the add-on is still read by its name.
        Assert.Equal(["Second", "Video Speed Controller"], extensions.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.All(extensions, e => Assert.Null(e.InstalledOn));
    }

    [Fact]
    public void APackageNotOnALocalDrive_IsNotOpened()
    {
        // The same package, written the way a network share or a device path is: such a path is never opened.
        var package = Package("""{ "manifest_version": 2, "name": "Far" }""", "icon.png", [5]);
        Write(Addon(extra: $$""", "path": "{{Json(@"\\?\" + package)}}", "icons": { "48": "icon.png" } """));

        var extension = Single();

        Assert.Null(extension.IconBytes);
        Assert.Equal("Video Speed Controller", extension.Name);
    }

    [Fact]
    public void AnAddOnWithABrokenEscape_IsListedAsUnreadable_AndTheRestAreListed()
    {
        Write(Addon(extra: ", \"\\udc00\\udc00\\udc00\\udc00\\udc00\": 0"), Addon("Second"));

        var (extensions, couldNotRead) = Read();

        Assert.False(couldNotRead);
        Assert.Equal([ChromiumExtensionReader.UnreadableName, "Second"], extensions.Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AListWithABrokenEscapeInItsOutline_CouldNotBeRead()
    {
        File.WriteAllText(Path.Combine(_profile, "extensions.json"),
            "{ \"addons\": [ " + Addon() + " ], \"\\udc00\\udc00\\udc00\\udc00\\udc00\": 0 }");

        var (extensions, couldNotRead) = Read();

        Assert.True(couldNotRead);
        Assert.Empty(extensions);
    }

    [Fact]
    public void APackageWithABrokenEscape_LeavesTheExtensionListed()
    {
        var package = Package("{ \"manifest_version\": 2, \"name\": \"Odd\", \"\\udc00\\udc00\\udc00\\udc00\\udc00\": 0 }", "icon.png", [5]);
        Write(Addon(extra: $$""", "path": "{{Json(package)}}", "icons": { "48": "icon.png" } """));

        Assert.Equal("Video Speed Controller", Single().Name);
    }

    [Fact]
    public void APackageInsideTheProfile_IsOpened_WhereverTheProfileIs()
    {
        // With Windows' folder redirection the whole profile can sit on a share (\\server\…): a package inside the
        // very profile being read is opened all the same. Written here as \\?\, the same folder off a drive letter.
        var package = Package("""{ "manifest_version": 2, "name": "Here" }""", "icon.png", [5]);
        Write(Addon(extra: $$""", "path": "{{Json(@"\\?\" + package)}}", "icons": { "48": "icon.png" } """));

        var (extensions, _) = FirefoxExtensionReader.Read(@"\\?\" + _profile);

        Assert.Equal([5], Assert.Single(extensions).IconBytes);
    }

    [Fact]
    public void AnExtensionWithNoName_IsStillListed()
    {
        Write("""{ "id": "x@example", "type": "extension", "version": "1", "location": "app-profile", "active": true }""");

        Assert.Equal(ChromiumExtensionReader.UnreadableName, Single().Name);
    }
}
