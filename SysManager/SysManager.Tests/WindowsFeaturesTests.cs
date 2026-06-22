// SysManager · WindowsFeaturesTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="WindowsFeaturesService"/> and <see cref="WindowsFeaturesViewModel"/>.
/// </summary>
public class WindowsFeaturesTests
{
    // ── ParseFeatureList ──

    [Fact]
    public void ParseFeatureList_EmptyInput_ReturnsEmpty()
    {
        var result = WindowsFeaturesService.ParseFeatureList(new List<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void ParseFeatureList_ValidLines_ParsesCorrectly()
    {
        var lines = new List<string>
        {
            "Microsoft-Hyper-V-All|Enabled",
            "TelnetClient|Disabled",
            "NetFx3|Enabled"
        };

        var result = WindowsFeaturesService.ParseFeatureList(lines);

        Assert.Equal(3, result.Count);
        var hyperV = result.First(f => f.Name == "Microsoft-Hyper-V-All");
        Assert.True(hyperV.IsEnabled);
        Assert.Equal("Virtualization", hyperV.Category);

        var telnet = result.First(f => f.Name == "TelnetClient");
        Assert.False(telnet.IsEnabled);
        Assert.Equal("Networking", telnet.Category);
    }

    [Fact]
    public void ParseFeatureList_SkipsBlankLines()
    {
        var lines = new List<string>
        {
            "",
            "  ",
            "SomeFeature|Disabled",
            ""
        };

        var result = WindowsFeaturesService.ParseFeatureList(lines);
        Assert.Single(result);
    }

    [Fact]
    public void ParseFeatureList_SkipsInvalidLines()
    {
        var lines = new List<string>
        {
            "NoSeparator",
            "|Enabled",
            "ValidFeature|Disabled"
        };

        var result = WindowsFeaturesService.ParseFeatureList(lines);
        Assert.Single(result);
        Assert.Equal("ValidFeature", result[0].Name);
    }

    // ── CategorizeFeature ──

    [Theory]
    [InlineData("Microsoft-Hyper-V-All", "Virtualization")]
    [InlineData("VirtualMachinePlatform", "Virtualization")]
    [InlineData("Microsoft-Windows-Subsystem-Linux", "Virtualization")]
    [InlineData("Containers", "Virtualization")]
    [InlineData("Windows-Sandbox", "Virtualization")]
    [InlineData("TelnetClient", "Networking")]
    [InlineData("IIS-WebServer", "Networking")]
    [InlineData("SMB1Protocol", "Networking")]
    [InlineData("NetFx3", "Development")]
    [InlineData("Microsoft-Windows-Developer-Mode", "Development")]
    [InlineData("OpenSSH-Client", "Development")]
    [InlineData("MediaPlayback", "Media & Print")]
    [InlineData("Printing-XPSServices-Features", "Media & Print")]
    [InlineData("DirectPlay", "Legacy")]
    [InlineData("WorkFolders-Client", "Legacy")]
    // Regression: a bare "WORK" substring used to drop any "...work..." feature into
    // Legacy. An unknown feature that merely contains "work" must NOT be Legacy now.
    [InlineData("SomeFrameworkThing", "Other")]
    [InlineData("SomeRandomFeature", "Other")]
    public void CategorizeFeature_AssignsCorrectCategory(string featureName, string expected)
    {
        Assert.Equal(expected, WindowsFeature.CategorizeFeature(featureName));
    }

    [Fact]
    public void CategorizeFeature_NullOrEmpty_ReturnsOther()
    {
        Assert.Equal("Other", WindowsFeature.CategorizeFeature(null!));
        Assert.Equal("Other", WindowsFeature.CategorizeFeature(""));
        Assert.Equal("Other", WindowsFeature.CategorizeFeature("   "));
    }

    // ── HumanizeName ──

    [Theory]
    [InlineData("Microsoft-Hyper-V-All", "Microsoft Hyper V All")]
    [InlineData("TelnetClient", "TelnetClient")]
    [InlineData("Some_Feature_Name", "Some Feature Name")]
    public void HumanizeName_ReplacesDelimiters(string input, string expected)
    {
        Assert.Equal(expected, WindowsFeaturesService.HumanizeName(input));
    }

    // ── ViewModel ──

    [Fact]
    public void ViewModel_InitialState_IsCorrect()
    {
        var vm = new WindowsFeaturesViewModel(new WindowsFeaturesService(new PowerShellRunner()));
        Assert.Empty(vm.AllFeatures);
        Assert.Empty(vm.FilteredFeatures);
        Assert.Equal("", vm.FilterText);
        Assert.Equal(0, vm.FeatureCount);
        Assert.False(vm.PendingReboot);
    }
}
