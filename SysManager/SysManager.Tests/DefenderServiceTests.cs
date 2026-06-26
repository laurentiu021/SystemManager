// SysManager · DefenderServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management.Automation;
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

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]   // out of range → clamp to 0
    [InlineData(-1, 0)]
    public void ClampTri_KeepsZeroToTwo(int input, int expected)
        => Assert.Equal(expected, DefenderService.ClampTri(input));

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
