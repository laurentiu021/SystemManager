// SysManager · WatchedSettingTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class WatchedSettingTests
{
    private static WatchedSetting MakeSetting() => new(
        "telemetry", "Diagnostic data", "desc", "Privacy",
        @"HKLM\SOFTWARE\Test", "AllowTelemetry",
        new Dictionary<int, string> { [0] = "Off", [3] = "Full" });

    [Fact]
    public void Describe_NullValue_IsNotSet()
        => Assert.Equal("Not set", MakeSetting().Describe(null));

    [Fact]
    public void Describe_MappedValue_UsesLabel()
    {
        var s = MakeSetting();
        Assert.Equal("Off", s.Describe(0));
        Assert.Equal("Full", s.Describe(3));
    }

    [Fact]
    public void Describe_UnmappedValue_FallsBackToNumber()
        => Assert.Equal("7", MakeSetting().Describe(7));

    [Fact]
    public void SettingDrift_Summary_ReadsInPlainLanguage()
    {
        var drift = new SettingDrift(MakeSetting(), BaselineValue: 0, CurrentValue: 3);
        Assert.Equal("Diagnostic data: was \"Off\", now \"Full\"", drift.Summary);
    }

    [Fact]
    public void SettingDrift_DefaultsToRestorable()
        => Assert.True(new SettingDrift(MakeSetting(), 0, 3).CanRestore);
}
