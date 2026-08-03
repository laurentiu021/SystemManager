// SysManager · NavGroupTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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
        var g = new NavGroup
        {
            Id = "test",
            Label = "Test",
            Glyph = "T",
            Subtitle = "A · B · C"
        };
        Assert.Equal("A · B · C", g.Subtitle);
    }

    [Fact]
    public void Tooltip_CanBeSet()
    {
        var g = new NavGroup
        {
            Id = "test",
            Label = "Test",
            Glyph = "T",
            Tooltip = "Alpha\nBeta\nGamma"
        };
        Assert.Contains("Alpha", g.Tooltip);
        Assert.Contains("Beta", g.Tooltip);
    }

    [Fact]
    public void IsSingleItem_FalseWhenMultipleChildren()
    {
        var g = new NavGroup
        {
            Id = "test",
            Label = "Test",
            Glyph = "T",
            Children = {
            new NavItem { Id = "a", Label = "A", Glyph = "A",
                Content = new object(), ViewType = typeof(object) },
            new NavItem { Id = "b", Label = "B", Glyph = "B",
                Content = new object(), ViewType = typeof(object) },
        }
        };
        Assert.False(g.IsSingleItem);
        Assert.Equal(2, g.ChildCount);
    }

    [Fact]
    public void IsExpanded_DefaultFalse()
    {
        var g = new NavGroup { Id = "test", Label = "Test", Glyph = "T" };
        Assert.False(g.IsExpanded);
    }

    [Fact]
    public void NavItem_IsInDevelopment_DefaultsFalse()
    {
        var item = new NavItem
        {
            Id = "a",
            Label = "A",
            Glyph = "A",
            Content = new object(),
            ViewType = typeof(object),
        };
        Assert.False(item.IsInDevelopment);
    }

    [Fact]
    public void NavItem_IsInDevelopment_SetViaInitializer()
    {
        var item = new NavItem
        {
            Id = "a",
            Label = "A",
            Glyph = "A",
            Content = new object(),
            ViewType = typeof(object),
            IsInDevelopment = true,
        };
        Assert.True(item.IsInDevelopment);
    }

    [Fact]
    public void NavItem_IsSelected_DefaultsFalseWithEmptyAutomationStatus()
    {
        var item = CreateNavItem();

        Assert.False(item.IsSelected);
        Assert.Equal(string.Empty, item.SelectionStatus);
    }

    [Fact]
    public void NavItem_IsSelected_UpdatesAutomationStatusAndRaisesChanges()
    {
        var item = CreateNavItem();
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.IsSelected = true;

        Assert.True(item.IsSelected);
        Assert.Equal("Selected", item.SelectionStatus);
        Assert.Contains(nameof(NavItem.IsSelected), changed);
        Assert.Contains(nameof(NavItem.SelectionStatus), changed);
    }

    [Fact]
    public void MainWindowSelection_TransfersStateBetweenItems()
    {
        var oldItem = CreateNavItem();
        var newItem = CreateNavItem();
        oldItem.IsSelected = true;

        MainWindowViewModel.UpdateSelectionState(oldItem, newItem);

        Assert.False(oldItem.IsSelected);
        Assert.True(newItem.IsSelected);
    }

    [Fact]
    public void MainWindowSelection_NullClearsOldState()
    {
        var oldItem = CreateNavItem();
        oldItem.IsSelected = true;

        MainWindowViewModel.UpdateSelectionState(oldItem, null);

        Assert.False(oldItem.IsSelected);
    }

    private static NavItem CreateNavItem() => new()
    {
        Id = "a",
        Label = "A",
        Glyph = "A",
        Content = new object(),
        ViewType = typeof(object),
    };
}
