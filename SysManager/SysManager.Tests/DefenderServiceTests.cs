// SysManager · DefenderServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
using NSubstitute;
using SysManager.Services;

namespace SysManager.Tests;

public class DefenderServiceTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("True", true)]
    [InlineData("False", false)]
    [InlineData(null, false)]
    public void ToBool_HandlesBoolAndStringAndNull(object? input, bool expected)
        => Assert.Equal(expected, DefenderService.ToBool(input));

    [Theory]
    [InlineData(2, 2)]
    [InlineData("1", 1)]
    [InlineData(null, 0)]
    [InlineData("garbage", 0)]
    public void ToInt_ParsesOrZero(object? input, int expected)
        => Assert.Equal(expected, DefenderService.ToInt(input));

    [Fact]
    public void ToStringList_FromArray()
    {
        var list = DefenderService.ToStringList(new object[] { @"C:\Games", @"D:\Steam", "" });
        Assert.Equal(2, list.Count);
        Assert.Contains(@"C:\Games", list);
        Assert.DoesNotContain("", list);
    }

    [Fact]
    public void ToStringList_FromSingleAndNull()
    {
        Assert.Single(DefenderService.ToStringList(@"C:\One"));
        Assert.Empty(DefenderService.ToStringList(null));
    }

    [Fact]
    public void ToStringList_FromRemotingCollectionShape()
    {
        var deserialized = new PSObject(new ArrayList
        {
            new PSObject(@"C:\Games"),
            new PSObject(@"D:\Steam"),
            new PSObject("")
        });

        var list = DefenderService.ToStringList(deserialized);

        Assert.Equal(new[] { @"C:\Games", @"D:\Steam" }, list);
    }

    [Fact]
    public async Task MutationScripts_DeclareAndBindTheirParameters()
    {
        const string path = @"C:\Games";
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Collection<PSObject>());
        var service = new DefenderService(runner);

        await service.SetPuaProtectionAsync(1);
        await service.SetControlledFolderAccessAsync(2);
        await service.AddExclusionPathAsync(path);
        await service.RemoveExclusionPathAsync(path);

        await runner.Received(1).RunAsync(
            "param([int]$Value) Set-MpPreference -PUAProtection $Value",
            Arg.Is<IDictionary<string, object?>?>(values =>
                values != null && Equals(values["Value"], 1)),
            Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(
            "param([int]$Value) Set-MpPreference -EnableControlledFolderAccess $Value",
            Arg.Is<IDictionary<string, object?>?>(values =>
                values != null && Equals(values["Value"], 2)),
            Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(
            "param([string]$Path) Add-MpPreference -ExclusionPath $Path",
            Arg.Is<IDictionary<string, object?>?>(values =>
                values != null && Equals(values["Path"], path)),
            Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(
            "param([string]$Path) Remove-MpPreference -ExclusionPath $Path",
            Arg.Is<IDictionary<string, object?>?>(values =>
                values != null && Equals(values["Path"], path)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]   // out of range → clamp to 0
    [InlineData(-1, 0)]
    public void ClampTri_KeepsZeroToTwo(int input, int expected)
        => Assert.Equal(expected, DefenderService.ClampTri(input));

    // ── Exclusion-path validation at the service boundary (idx 151) ───────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\path")]   // not rooted
    [InlineData(@"C:\Games\*")]       // wildcard
    [InlineData(@"C:\Games\?")]       // wildcard
    public void IsValidExclusionPath_RejectsBadInput(string? path)
        => Assert.False(DefenderService.IsValidExclusionPath(path!));

    [Theory]
    [InlineData(@"C:\Games")]
    [InlineData(@"D:\Steam\steamapps")]
    public void IsValidExclusionPath_AcceptsRootedNonWildcardPath(string path)
        => Assert.True(DefenderService.IsValidExclusionPath(path));

    [Fact]
    public void ParseStatus_NormalizesInvertedRealtimeBoolean()
    {
        // DisableRealtimeMonitoring = true means real-time protection is OFF.
        var obj = new PSObject();
        obj.Properties.Add(new PSNoteProperty("DisableRealtimeMonitoring", true));
        obj.Properties.Add(new PSNoteProperty("PUAProtection", 1));
        obj.Properties.Add(new PSNoteProperty("MAPSReporting", 2));
        obj.Properties.Add(new PSNoteProperty("EnableControlledFolderAccess", 0));
        obj.Properties.Add(new PSNoteProperty("ExclusionPath", new object[] { @"C:\Games" }));
        obj.Properties.Add(new PSNoteProperty("ExclusionExtension", new object[] { }));
        obj.Properties.Add(new PSNoteProperty("ExclusionProcess", new object[] { }));
        obj.Properties.Add(new PSNoteProperty("IsTamperProtected", true));

        var status = DefenderService.ParseStatus(obj);

        Assert.True(status.Available);
        Assert.False(status.RealtimeProtection); // inverted: Disable=true → protection OFF
        Assert.True(status.IsTamperProtected);
        Assert.Equal(1, status.PuaProtection);
        Assert.Equal(2, status.MapsReporting);
        Assert.Equal(0, status.ControlledFolderAccess);
        Assert.Single(status.ExclusionPaths);
    }

    [Fact]
    public void ParseStatus_RealtimeOn_WhenDisableFalse()
    {
        var obj = new PSObject();
        obj.Properties.Add(new PSNoteProperty("DisableRealtimeMonitoring", false));
        var status = DefenderService.ParseStatus(obj);
        Assert.True(status.RealtimeProtection);
    }
}
