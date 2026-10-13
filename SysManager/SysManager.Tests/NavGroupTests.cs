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
            new NavItem { Id = "a", Label = "A",
                Content = new object(), ViewType = typeof(object) },
            new NavItem { Id = "b", Label = "B",
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
        Assert.Equal(string.Empty, item.ItemStatus);
    }

    [Fact]
    public void NavItem_IsSelected_UpdatesAutomationStatusAndRaisesChanges()
    {
        var item = CreateNavItem();
        var changed = item.RecordPropertyChanges();

        item.IsSelected = true;

        Assert.True(item.IsSelected);
        Assert.Equal("Selected", item.ItemStatus);
        Assert.Contains(nameof(NavItem.IsSelected), changed);
        Assert.Contains(nameof(NavItem.ItemStatus), changed);
    }

    /// <summary>
    /// A tab still being verified says so to a screen reader, selected or not.
    /// </summary>
    /// <remarks>
    /// The word "Preview" under the name is drawn inside the row, but the row's accessible name is the label alone,
    /// so without this a screen reader never said it (#2686). A plain tab keeps exactly the status it had, because
    /// the UI tests and Narrator users already rely on "Selected".
    /// </remarks>
    [Theory]
    [InlineData(false, false, "")]
    [InlineData(true, false, "Selected")]
    [InlineData(false, true, "Preview")]
    [InlineData(true, true, "Selected, preview")]
    public void NavItem_ItemStatus_SaysPreviewForATabStillBeingVerified(bool selected, bool preview, string expected)
    {
        var item = new NavItem
        {
            Id = "a",
            Label = "A",
            Content = new object(),
            ViewType = typeof(object),
            IsInDevelopment = preview,
        };

        item.IsSelected = selected;

        Assert.Equal(expected, item.ItemStatus);
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
        Content = new object(),
        ViewType = typeof(object),
    };
}
