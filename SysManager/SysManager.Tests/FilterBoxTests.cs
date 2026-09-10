// SysManager · FilterBoxTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="FilterBoxes.IsFilterBindingPath"/> — which <c>Text</c> bindings Ctrl+F treats as
/// the tab's filter box.
/// </summary>
/// <remarks>
/// The visual-tree walk around it needs real WPF elements on an STA thread, so what is pinned here is the
/// decision: given a binding path, is this the box. Which views bind which name is held by
/// <c>ArchitectureTests.EveryFilterBox_BindsANameCtrlFRecognises</c> against the XAML.
/// </remarks>
public class FilterBoxTests
{
    [Theory]
    [InlineData("FilterText")]
    [InlineData("SearchText")]
    [InlineData("SearchQuery")]
    [InlineData("Filter")]
    public void TheFourNamesTheViewsActuallyBind_AreRecognised(string path)
        => Assert.True(FilterBoxes.IsFilterBindingPath(path));

    [Fact]
    public void PlainFilter_IsRecognised_BecauseTaskSchedulerBindsIt()
    {
        // Kept as its own test rather than only a row above, because this is the one that was missing.
        // Three names looked like the whole set; a scan of every TextBox announced as a filter or search
        // box found Task Scheduler binding plain "Filter", where Ctrl+F would have done nothing at all.
        Assert.True(FilterBoxes.IsFilterBindingPath("Filter"));
        Assert.Contains("Filter", FilterBoxes.BindingPaths);
    }

    [Theory]
    [InlineData("FilterTextLength")]
    [InlineData("SearchTextPlaceholder")]
    [InlineData("HasFilterText")]
    [InlineData("filtertext")]
    [InlineData("Filters")]
    public void ANameThatMerelyLooksLikeAFilter_IsNotRecognised(string path)
    {
        // Exact, ordinal match on purpose. A prefix or contains rule would let the caret land in a
        // different box on the same tab, and a shortcut that focuses the wrong control is worse than one
        // that does nothing — the user types a search into whatever had focus.
        Assert.False(FilterBoxes.IsFilterBindingPath(path));
    }

    [Fact]
    public void AnUnboundTextBox_IsNotAFilter()
    {
        // FindIn passes null through for a TextBox whose Text is not bound at all, which is most of them:
        // an editable cell, a hosts-file entry, a new-variable name.
        Assert.False(FilterBoxes.IsFilterBindingPath(null));
        Assert.False(FilterBoxes.IsFilterBindingPath(""));
    }

    [Fact]
    public void TheRecogniserHasNoDuplicatesAndNoBlanks()
    {
        // A duplicate would be harmless and a blank would match every unbound box, which is the one entry
        // that could make Ctrl+F land somewhere arbitrary.
        Assert.Equal(FilterBoxes.BindingPaths.Length, FilterBoxes.BindingPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("", FilterBoxes.BindingPaths);
    }
}
