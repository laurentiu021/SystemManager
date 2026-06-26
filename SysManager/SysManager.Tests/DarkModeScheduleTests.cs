// SysManager · DarkModeScheduleTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class DarkModeScheduleTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        var s = new DarkModeSchedule();
        Assert.False(s.Enabled);
        Assert.True(s.ApplyToSystem);
        Assert.Equal(new TimeOnly(19, 0), s.DarkStartTime);
        Assert.Equal(new TimeOnly(7, 0), s.LightStartTime);
    }

    [Fact]
    public void ParsesValidTimes()
    {
        var s = new DarkModeSchedule { DarkStart = "21:30", LightStart = "06:15" };
        Assert.Equal(new TimeOnly(21, 30), s.DarkStartTime);
        Assert.Equal(new TimeOnly(6, 15), s.LightStartTime);
    }

    [Fact]
    public void FallsBackOnGarbageTime()
    {
        var s = new DarkModeSchedule { DarkStart = "not-a-time", LightStart = "" };
        Assert.Equal(new TimeOnly(19, 0), s.DarkStartTime);
        Assert.Equal(new TimeOnly(7, 0), s.LightStartTime);
    }
}
