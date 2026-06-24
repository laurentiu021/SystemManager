// SysManager · DebloaterServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management.Automation;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DebloaterService"/>'s pure logic — the denylist
/// (<see cref="DebloaterService.IsProtected"/>) and the <c>Get-AppxPackage</c> parser
/// (<see cref="DebloaterService.ParsePackages"/>). PSObjects are built in memory so the
/// tests need no live Appx subsystem. The actual Remove-AppxPackage path touches the OS
/// and is not unit-tested.
/// </summary>
public class DebloaterServiceTests
{
    private static PSObject MakePkg(string? name, string? full, string? family, string? publisher = "CN=Microsoft", string? version = "1.0.0.0")
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("Name", name));
        o.Properties.Add(new PSNoteProperty("PackageFullName", full));
        o.Properties.Add(new PSNoteProperty("PackageFamilyName", family));
        o.Properties.Add(new PSNoteProperty("Publisher", publisher));
        o.Properties.Add(new PSNoteProperty("Version", version));
        return o;
    }

    // ---------- IsProtected (denylist) ----------

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.WindowsStore_22210.1402.7.0_x64__8wekyb3d8bbwe")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.VCLibs.140.00")]
    [InlineData("Microsoft.NET.Native.Framework.2.2")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.Windows.ShellExperienceHost")]
    [InlineData("Microsoft.SecHealthUI")]
    public void IsProtected_TrueForSystemCriticalPackages(string name)
        => Assert.True(DebloaterService.IsProtected(name));

    [Theory]
    [InlineData("Microsoft.BingNews")]
    [InlineData("Microsoft.MicrosoftSolitaireCollection")]
    [InlineData("Clipchamp.Clipchamp")]
    [InlineData("SpotifyAB.SpotifyMusic")]
    public void IsProtected_FalseForRemovableApps(string name)
        => Assert.False(DebloaterService.IsProtected(name));

    [Fact]
    public void IsProtected_IsCaseInsensitive()
        => Assert.True(DebloaterService.IsProtected("microsoft.windowsstore_x64"));

    // ---------- ParsePackages ----------

    [Fact]
    public void Parse_MapsFieldsAndFlags()
    {
        var rows = new[] { MakePkg("Microsoft.BingNews", "Microsoft.BingNews_1.2_x64__abc", "Microsoft.BingNews_abc") };
        var result = DebloaterService.ParsePackages(rows);

        Assert.Single(result);
        var app = result[0];
        Assert.Equal("Microsoft.BingNews", app.Name);
        Assert.Equal("Microsoft News", app.DisplayName);   // from catalog
        Assert.True(app.IsCommonBloat);
        Assert.False(app.IsProtected);
        Assert.NotEqual("", app.Description);
    }

    [Fact]
    public void Parse_FlagsProtectedPackages()
    {
        var rows = new[] { MakePkg("Microsoft.WindowsStore", "Microsoft.WindowsStore_1_x64__abc", "Microsoft.WindowsStore_abc") };
        var result = DebloaterService.ParsePackages(rows);
        Assert.Single(result);
        Assert.True(result[0].IsProtected);
        Assert.False(result[0].IsCommonBloat); // never both
    }

    [Fact]
    public void Parse_SkipsRowsMissingIdentity()
    {
        var rows = new[]
        {
            MakePkg(null, "full", "family"),
            MakePkg("Name", null, "family"),
            MakePkg("Name", "full", null),
            MakePkg("Microsoft.BingWeather", "Microsoft.BingWeather_1_x64__abc", "Microsoft.BingWeather_abc"),
        };
        var result = DebloaterService.ParsePackages(rows);
        Assert.Single(result);
        Assert.Equal("Microsoft.BingWeather", result[0].Name);
    }

    [Fact]
    public void Parse_OrdersCommonBloatFirst()
    {
        var rows = new[]
        {
            MakePkg("Contoso.RandomApp", "Contoso.RandomApp_1_x64__abc", "Contoso.RandomApp_abc"),
            MakePkg("Microsoft.BingNews", "Microsoft.BingNews_1_x64__abc", "Microsoft.BingNews_abc"),
        };
        var result = DebloaterService.ParsePackages(rows);
        Assert.Equal("Microsoft.BingNews", result[0].Name); // common bloat sorts first
        Assert.Equal("Contoso.RandomApp", result[1].Name);
    }

    [Fact]
    public void Parse_UnknownApp_GetsPrettifiedDisplayName()
    {
        var rows = new[] { MakePkg("Contoso.SuperWidgetApp", "Contoso.SuperWidgetApp_1_x64__abc", "Contoso.SuperWidgetApp_abc") };
        var result = DebloaterService.ParsePackages(rows);
        Assert.Single(result);
        // "SuperWidgetApp" -> "Super Widget App"
        Assert.Equal("Super Widget App", result[0].DisplayName);
        Assert.False(result[0].IsCommonBloat);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
        => Assert.Empty(DebloaterService.ParsePackages([]));
}
