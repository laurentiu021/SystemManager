// SysManager · PowerShellLineTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class PowerShellLineTests
{
    [Fact]
    public void Info_SetsKindInfo()
    {
        var l = PowerShellLine.Info("msg");
        Assert.Equal(OutputKind.Info, l.Kind);
        Assert.Equal("msg", l.Text);
    }

    [Fact]
    public void Output_SetsKindOutput()
    {
        var l = PowerShellLine.Output("data");
        Assert.Equal(OutputKind.Output, l.Kind);
    }

    [Fact]
    public void Warn_SetsKindWarning()
    {
        var l = PowerShellLine.Warn("warn");
        Assert.Equal(OutputKind.Warning, l.Kind);
    }

    [Fact]
    public void Err_SetsKindError()
    {
        var l = PowerShellLine.Err("err");
        Assert.Equal(OutputKind.Error, l.Kind);
    }

    [Fact]
    public void Timestamp_IsRecent()
    {
        var before = DateTime.Now.AddSeconds(-1);
        var l = PowerShellLine.Info("x");
        var after = DateTime.Now.AddSeconds(1);
        Assert.InRange(l.Timestamp, before, after);
    }

    [Fact]
    public void Records_Compare_ByValue()
    {
        var a = new PowerShellLine(OutputKind.Output, "same", DateTime.UnixEpoch);
        var b = new PowerShellLine(OutputKind.Output, "same", DateTime.UnixEpoch);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(OutputKind.Info)]
    [InlineData(OutputKind.Output)]
    [InlineData(OutputKind.Warning)]
    [InlineData(OutputKind.Error)]
    [InlineData(OutputKind.Verbose)]
    [InlineData(OutputKind.Debug)]
    [InlineData(OutputKind.Progress)]
    public void AllKinds_Roundtrip(OutputKind kind)
    {
        var l = new PowerShellLine(kind, "hello", DateTime.Now);
        Assert.Equal(kind, l.Kind);
    }

    [Fact]
    public void EmptyText_IsAllowed()
    {
        var l = PowerShellLine.Output("");
        Assert.Equal("", l.Text);
    }
}
