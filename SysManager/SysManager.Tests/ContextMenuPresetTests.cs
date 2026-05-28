// SysManager · ContextMenuPresetTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class ContextMenuPresetTests
{
    [Fact]
    public void All_ContainsFivePresets()
    {
        Assert.Equal(5, ContextMenuPreset.All.Count);
    }

    [Theory]
    [InlineData("win10")]
    [InlineData("win11")]
    [InlineData("minimal")]
    [InlineData("developer")]
    [InlineData("power")]
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
    public void Power_EnablesAll()
    {
        var preset = ContextMenuPreset.All["power"];
        var entry = new ContextMenuEntry
        {
            Name = "SomeRandomEntry",
            Command = "whatever.exe",
            RegistryPath = @"HKCR\*\shell\test",
            Location = "Files",
            RawName = "SomeRandomEntry"
        };
        Assert.True(preset.ShouldEnable(entry));
    }

    [Fact]
    public void Minimal_EnablesOpen()
    {
        var preset = ContextMenuPreset.All["minimal"];
        var entry = new ContextMenuEntry
        {
            Name = "Open",
            Command = "\"%1\"",
            RegistryPath = @"HKCR\*\shell\open",
            Location = "Files",
            RawName = "open"
        };
        Assert.True(preset.ShouldEnable(entry));
    }

    [Fact]
    public void Minimal_DisablesGitBash()
    {
        var preset = ContextMenuPreset.All["minimal"];
        var entry = new ContextMenuEntry
        {
            Name = "Git Bash Here",
            Command = "git-bash.exe",
            RegistryPath = @"HKCR\Directory\Background\shell\git_bash",
            Location = "Directory Background",
            RawName = "git_bash"
        };
        Assert.False(preset.ShouldEnable(entry));
    }

    [Fact]
    public void Developer_EnablesGitBash()
    {
        var preset = ContextMenuPreset.All["developer"];
        var entry = new ContextMenuEntry
        {
            Name = "Git Bash Here",
            Command = "git-bash.exe",
            RegistryPath = @"HKCR\Directory\Background\shell\git_bash",
            Location = "Directory Background",
            RawName = "git_bash"
        };
        Assert.True(preset.ShouldEnable(entry));
    }

    [Fact]
    public void Developer_DisablesShare()
    {
        var preset = ContextMenuPreset.All["developer"];
        var entry = new ContextMenuEntry
        {
            Name = "Share",
            Command = "shell32.dll",
            RegistryPath = @"HKCR\*\shell\Windows.ModernShare",
            Location = "Files",
            RawName = "Windows.ModernShare"
        };
        Assert.False(preset.ShouldEnable(entry));
    }
}
