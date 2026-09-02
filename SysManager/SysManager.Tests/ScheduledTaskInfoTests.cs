// SysManager · ScheduledTaskInfoTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class ScheduledTaskInfoTests
{
    private static ScheduledTaskInfo Make(string state, TaskCategory cat = TaskCategory.ThirdParty)
        => new("MyTask", @"\Vendor\", state, "Vendor", "desc", cat, null, null);

    [Theory]
    [InlineData("Ready", true)]
    [InlineData("Running", true)]
    [InlineData("Queued", true)]
    [InlineData("Disabled", false)]
    public void IsEnabled_TrueUnlessDisabled(string state, bool expected)
        => Assert.Equal(expected, Make(state).IsEnabled);

    [Fact]
    public void FullPath_CombinesPathAndName()
        => Assert.Equal(@"\Vendor\MyTask", Make("Ready").FullPath);

    [Theory]
    [InlineData(TaskCategory.Telemetry, "Telemetry")]
    [InlineData(TaskCategory.System, "System")]
    [InlineData(TaskCategory.ThirdParty, "Third-party")]
    public void CategoryLabel_Maps(TaskCategory cat, string label)
        => Assert.Equal(label, Make("Ready", cat).CategoryLabel);

    [Fact]
    public void RunDisplays_ShowDashWhenNull()
    {
        var t = Make("Ready");
        Assert.Equal("—", t.LastRunDisplay);
        Assert.Equal("—", t.NextRunDisplay);
    }

    [Fact]
    public void IsSystem_TrueOnlyForSystemCategory()
    {
        Assert.True(Make("Ready", TaskCategory.System).IsSystem);
        Assert.False(Make("Ready", TaskCategory.Telemetry).IsSystem);
    }

    private static ScheduledTaskInfo WithDescription(string? description)
        => new("MyTask", @"\Vendor\", "Ready", "Vendor", description, TaskCategory.ThirdParty, null, null);

    /// <summary>
    /// A great many Windows tasks report no description. Whitespace counts as none: a value of " "
    /// renders exactly like an empty cell, so treating it as real text would leave the blank the
    /// fallback exists to remove.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void DescriptionDisplay_SaysSoWhenWindowsGivesNothing(string? description)
        => Assert.Equal(
            "No description provided by Windows.",
            WithDescription(description).DescriptionDisplay);

    [Fact]
    public void DescriptionDisplay_PassesWindowsOwnWordsThrough()
        => Assert.Equal(
            "Keeps your printer working.",
            WithDescription("Keeps your printer working.").DescriptionDisplay);
}
