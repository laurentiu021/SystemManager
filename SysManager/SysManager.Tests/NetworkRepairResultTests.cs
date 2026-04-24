// SysManager · NetworkRepairResultTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class NetworkRepairResultTests
{
    [Fact]
    public void Record_StoresAllProperties()
    {
        var r = new NetworkRepairResult("DNS Flush", true, "Success", false);

        Assert.Equal("DNS Flush", r.ToolName);
        Assert.True(r.Success);
        Assert.Equal("Success", r.Output);
        Assert.False(r.NeedsReboot);
    }

    [Fact]
    public void Record_NeedsReboot_True()
    {
        var r = new NetworkRepairResult("Winsock Reset", true, "Done", true);

        Assert.True(r.NeedsReboot);
    }

    [Fact]
    public void Record_Failure()
    {
        var r = new NetworkRepairResult("TCP/IP Reset", false, "Access denied", true);

        Assert.False(r.Success);
        Assert.Equal("Access denied", r.Output);
    }

    [Fact]
    public void Record_Equality()
    {
        var a = new NetworkRepairResult("DNS Flush", true, "OK", false);
        var b = new NetworkRepairResult("DNS Flush", true, "OK", false);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Record_Inequality_DifferentTool()
    {
        var a = new NetworkRepairResult("DNS Flush", true, "OK", false);
        var b = new NetworkRepairResult("Winsock Reset", true, "OK", false);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Record_Inequality_DifferentSuccess()
    {
        var a = new NetworkRepairResult("DNS Flush", true, "OK", false);
        var b = new NetworkRepairResult("DNS Flush", false, "OK", false);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Record_EmptyOutput()
    {
        var r = new NetworkRepairResult("DNS Flush", true, "", false);

        Assert.Equal("", r.Output);
    }

    [Fact]
    public void Record_MultilineOutput()
    {
        var output = "Line 1\nLine 2\nLine 3";
        var r = new NetworkRepairResult("DNS Flush", true, output, false);

        Assert.Contains("Line 2", r.Output);
    }

    [Fact]
    public void Record_ToString_ContainsToolName()
    {
        var r = new NetworkRepairResult("DNS Flush", true, "OK", false);

        Assert.Contains("DNS Flush", r.ToString());
    }

    [Fact]
    public void Record_With_ChangesProperty()
    {
        var r = new NetworkRepairResult("DNS Flush", true, "OK", false);
        var modified = r with { Success = false };

        Assert.True(r.Success);
        Assert.False(modified.Success);
    }
}
