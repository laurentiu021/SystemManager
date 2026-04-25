// SysManager · NavGroupTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

public class NavGroupTests
{
    [Fact]
    public void ChildCount_ReflectsChildren()
    {
        var g = new NavGroup { Id = "test", Label = "Test", Glyph = "T" };
        Assert.Equal(0, g.ChildCount);
    }

    [Fact]
    public void Subtitle_DefaultEmpty()
    {
        var g = new NavGroup { Id = "test", Label = "Test", Glyph = "T" };
        Assert.Equal("", g.Subtitle);
    }

    [Fact]
    public void Tooltip_DefaultEmpty()
    {
        var g = new NavGroup { Id = "test", Label = "Test", Glyph = "T" };
        Assert.Equal("", g.Tooltip);
    }

    [Fact]
    public void Subtitle_CanBeSet()
    {
        var g = new NavGroup { Id = "test", Label = "Test", Glyph = "T",
            Subtitle = "A · B · C" };
        Assert.Equal("A · B · C", g.Subtitle);
    }

    [Fact]
    public void Tooltip_CanBeSet()
    {
        var g = new NavGroup { Id = "test", Label = "Test", Glyph = "T",
            Tooltip = "Alpha\nBeta\nGamma" };
        Assert.Contains("Alpha", g.Tooltip);
        Assert.Contains("Beta", g.Tooltip);
    }

    [Fact]
    public void IsSingleItem_FalseWhenMultipleChildren()
    {
        var g = new NavGroup { Id = "test", Label = "Test", Glyph = "T", Children = {
            new NavItem { Id = "a", Label = "A", Glyph = "A",
                Content = new object(), ViewType = typeof(object) },
            new NavItem { Id = "b", Label = "B", Glyph = "B",
                Content = new object(), ViewType = typeof(object) },
        }};
        Assert.False(g.IsSingleItem);
        Assert.Equal(2, g.ChildCount);
    }

    [Fact]
    public void IsExpanded_DefaultTrue()
    {
        var g = new NavGroup { Id = "test", Label = "Test", Glyph = "T" };
        Assert.True(g.IsExpanded);
    }
}
