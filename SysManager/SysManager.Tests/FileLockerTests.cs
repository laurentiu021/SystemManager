// SysManager · FileLockerTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class FileLockerTests
{
    [Fact]
    public void Display_CombinesNameAndPid()
    {
        var l = new FileLocker(1234, "explorer.exe", "RmExplorer", null);
        Assert.Equal("explorer.exe (1234)", l.Display);
    }

    [Fact]
    public void StartTimeDisplay_ShowsDashWhenNull()
    {
        var l = new FileLocker(1, "a.exe", "RmMainWindow", null);
        Assert.Equal("—", l.StartTimeDisplay);
    }

    [Fact]
    public void StartTimeDisplay_FormatsWhenPresent()
    {
        var when = new DateTime(2026, 6, 26, 14, 30, 5, DateTimeKind.Local);
        var l = new FileLocker(1, "a.exe", "RmMainWindow", when);
        Assert.Equal("2026-06-26 14:30:05", l.StartTimeDisplay);
    }

    [Theory]
    [InlineData("RmCritical", true)]
    [InlineData("rmcritical", true)]
    [InlineData("RmMainWindow", false)]
    [InlineData("RmService", false)]
    public void IsCritical_TrueOnlyForCriticalAppType(string appType, bool expected)
    {
        var l = new FileLocker(1, "a.exe", appType, null);
        Assert.Equal(expected, l.IsCritical);
    }
}
