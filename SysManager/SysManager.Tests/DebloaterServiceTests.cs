// SysManager · DebloaterServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using NSubstitute;
using SysManager.Models;
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
    // Each value below is the EXACT Get-AppxPackage `.Name` (the family-name prefix) of a
    // system-critical package, confirmed against a real `Get-AppxPackage | Select Name`
    // capture from a Windows 11 machine — NOT echoed from the denylist constants. A
    // too-specific or misspelled denylist prefix would make StartsWith fail and silently
    // let a critical package be removed (the class of bug that hit Photos with the old
    // `...Photos.Settings` entry). Verifying against the real `.Name` is what prevents that.
    //
    // The bare `.Name` (as Windows reports it for these system packages):
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.Windows.ShellExperienceHost")]
    [InlineData("Microsoft.Windows.StartMenuExperienceHost")]
    [InlineData("Microsoft.SecHealthUI")]                    // verified present — NOT "Microsoft.Windows.SecHealthUI"
    [InlineData("Microsoft.AccountsControl")]
    [InlineData("Microsoft.AAD.BrokerPlugin")]
    [InlineData("Microsoft.LockApp")]
    [InlineData("Microsoft.CredDialogHost")]
    [InlineData("Microsoft.Win32WebViewHost")]
    [InlineData("Microsoft.Windows.CloudExperienceHost")]
    [InlineData("Microsoft.Windows.PeopleExperienceHost")]
    [InlineData("Microsoft.Windows.NarratorQuickStart")]
    [InlineData("Microsoft.XboxGameCallableUI")]
    [InlineData("Microsoft.Windows.Photos")]                 // regression: was ...Photos.Settings (too specific)
    [InlineData("Microsoft.Windows.ContentDeliveryManager")]
    [InlineData("Microsoft.StorePurchaseApp")]
    // Versioned frameworks: the real `.Name` carries a version segment, so the denylist
    // prefix (e.g. "Microsoft.VCLibs") must match the LONGER real name, not equal it.
    [InlineData("Microsoft.VCLibs.140.00")]
    [InlineData("Microsoft.VCLibs.140.00.UWPDesktop")]
    [InlineData("Microsoft.NET.Native.Framework.2.2")]
    [InlineData("Microsoft.NET.Native.Runtime.2.2")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.WindowsAppRuntime.1.8")]
    [InlineData("MicrosoftWindows.Client.Core")]             // Win11 client family (covers search/start host)
    [InlineData("MicrosoftWindows.Client.CBS")]
    // PackageFullName form (with _version_arch__hash) must also be protected via prefix.
    [InlineData("Microsoft.WindowsStore_22210.1402.7.0_x64__8wekyb3d8bbwe")]
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

    // ---------- RemoveAsync safety gates ----------

    private static StoreApp App(string name, string full, bool isProtected = false) => new()
    {
        Name = name,
        PackageFullName = full,
        PackageFamilyName = name + "_abc",
        DisplayName = name,
        Publisher = "CN=Microsoft",
        Version = "1.0.0.0",
        IsProtected = isProtected
    };

    [Fact]
    public async Task RemoveAsync_RefusesProtectedPackage_WithoutInvokingRunner()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var svc = new DebloaterService(runner);

        // Protected by name even if the flag was (somehow) cleared by a tampered binding.
        var result = await svc.RemoveAsync(App("Microsoft.Windows.Photos", "Microsoft.Windows.Photos_1_x64__abc"));

        Assert.False(result);
        await runner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("App.Name'; Remove-Item C:\\ -Recurse #")]
    [InlineData("App Name with spaces")]
    [InlineData("App$(rm)")]
    public async Task RemoveAsync_RejectsInjectionInFullName_WithoutInvokingRunner(string badFull)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var svc = new DebloaterService(runner);

        var result = await svc.RemoveAsync(App("Contoso.App", badFull));

        Assert.False(result);
        await runner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_ValidRemovableApp_InvokesRunner_AndConfirmsSuccess()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject> { PSObject.AsPSObject("__SM_RM_OK__") });
        var svc = new DebloaterService(runner);

        var result = await svc.RemoveAsync(App("Contoso.RandomApp", "Contoso.RandomApp_1.0.0.0_x64__abc"));

        Assert.True(result);
        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null && s.Contains("Remove-AppxPackage") && s.Contains("Contoso.RandomApp_1.0.0.0_x64__abc")),
            Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }
}
