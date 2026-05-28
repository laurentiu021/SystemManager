// SysManager · ContextMenuPresetTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class ContextMenuPresetTests
{
    [Fact]
    public void All_ContainsThreePresets()
    {
        Assert.Equal(3, ContextMenuPreset.All.Count);
    }

    [Theory]
    [InlineData("win10")]
    [InlineData("win11")]
    [InlineData("custom")]
    public void All_ContainsExpectedIds(string id)
    {
        Assert.True(ContextMenuPreset.All.ContainsKey(id));
    }

    [Fact]
    public void Win10_ForcesClassicMenu()
    {
        Assert.True(ContextMenuPreset.All["win10"].ForcesClassicMenu);
    }

    [Fact]
    public void Win11_DoesNotForceClassicMenu()
    {
        Assert.False(ContextMenuPreset.All["win11"].ForcesClassicMenu);
    }

    [Fact]
    public void Custom_DoesNotForceClassicMenu()
    {
        Assert.False(ContextMenuPreset.All["custom"].ForcesClassicMenu);
    }

    [Fact]
    public void AllPresets_HaveDescription()
    {
        foreach (var preset in ContextMenuPreset.All.Values)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Description));
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
        }
    }
}
