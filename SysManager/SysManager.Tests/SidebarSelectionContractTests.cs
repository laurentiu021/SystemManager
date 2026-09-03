// SysManager · SidebarSelectionContractTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Xml.Linq;

namespace SysManager.Tests;

public class SidebarSelectionContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void LiveRows_ShareSelectedVisualTreatment()
    {
        var document = LoadProjectXaml("MainWindow.xaml");
        var rowStyle = FindStyle(document, "SidebarNavRow");
        var rowTrigger = FindSelectedTrigger(rowStyle);

        AssertSetter(rowTrigger, "Background", "{DynamicResource AccentSoft}");
        var hoverTrigger = rowStyle
            .Descendants(Presentation + "Trigger")
            .Single(trigger =>
                (string?)trigger.Attribute("Property") == "IsMouseOver"
                && (string?)trigger.Attribute("Value") == "True");
        AssertSetter(hoverTrigger, "Background", "{DynamicResource RowHover}");

        var textStyle = FindStyle(document, "SidebarNavText");
        AssertSetter(textStyle, "Foreground", "{DynamicResource TextSecondary}");
        AssertSetter(textStyle, "FontWeight", "Medium");
        var textTrigger = FindSelectedTrigger(textStyle);
        AssertSetter(textTrigger, "Foreground", "{DynamicResource TextPrimary}");
        AssertSetter(textTrigger, "FontWeight", "SemiBold");

        var markStyle = FindStyle(document, "SidebarActiveMark");
        AssertSetter(markStyle, "Visibility", "Collapsed");
        var markTrigger = FindSelectedTrigger(markStyle);
        AssertSetter(markTrigger, "Visibility", "Visible");

        var liveRows = document
            .Descendants(Presentation + "Border")
            .Where(element =>
                (string?)element.Attribute("Style") == "{StaticResource SidebarNavRow}")
            .ToList();
        var liveMarks = document
            .Descendants(Presentation + "Rectangle")
            .Where(element =>
                (string?)element.Attribute("Style") == "{StaticResource SidebarActiveMark}")
            .ToList();
        var liveTexts = document
            .Descendants(Presentation + "TextBlock")
            .Where(element =>
                (string?)element.Attribute("Style") == "{StaticResource SidebarNavText}")
            .ToList();

        Assert.Equal(2, liveRows.Count);
        Assert.Equal(2, liveMarks.Count);
        // Three, and which three matters: the single-item row's glyph and label, and the leaf row's label.
        // A leaf has no glyph of its own — the group above it carries the icon — so a fourth here would
        // mean the empty gutter came back.
        Assert.Equal(3, liveTexts.Count);
        Assert.All(liveRows, row => Assert.Null(row.Attribute("Background")));
        Assert.All(liveMarks, mark => Assert.Null(mark.Attribute("Visibility")));
        Assert.All(
            liveRows,
            row => Assert.Single(
                row.Descendants(Presentation + "Rectangle"),
                mark => (string?)mark.Attribute("Style") == "{StaticResource SidebarActiveMark}"));
    }

    [Fact]
    public void LiveRows_ExposeSelectionToInvokableAutomationPeers()
    {
        var document = LoadProjectXaml("MainWindow.xaml");
        var singleRow = document
            .Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "SingleBd");
        var groupedRow = document
            .Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ChildBd");

        AssertNavButton(
            singleRow,
            idBinding: "{Binding Children[0].Id}",
            nameBinding: "{Binding Children[0].Label}",
            statusBinding: "{Binding Children[0].SelectionStatus}",
            tagBinding: "{Binding Children[0]}",
            clickHandler: "SingleGroup_Click");
        AssertNavButton(
            groupedRow,
            idBinding: "{Binding Id}",
            nameBinding: "{Binding Label}",
            statusBinding: "{Binding SelectionStatus}",
            tagBinding: "{Binding}",
            clickHandler: "NavChild_Click");

        var singleVisual = singleRow
            .Descendants(Presentation + "Border")
            .Single(element =>
                (string?)element.Attribute("Style") == "{StaticResource SidebarNavRow}");
        Assert.Equal(
            "{Binding Children[0]}",
            (string?)singleVisual.Attribute("DataContext"));

        var buttonStyle = FindStyle(document, "SidebarNavButton");
        var focusTrigger = buttonStyle
            .Descendants(Presentation + "Trigger")
            .Single(trigger =>
                (string?)trigger.Attribute("Property") == "IsKeyboardFocused"
                && (string?)trigger.Attribute("Value") == "True");
        AssertSetter(focusTrigger, "BorderBrush", "{DynamicResource Accent}");

        // Focusable is necessary but not sufficient: ButtonBase treats Enter as a click only where
        // this is set, so without it a row could be tabbed to and then not opened with the key most
        // people press. Asserted on the style, which is what both the leaf and single-item rows use.
        AssertSetter(buttonStyle, "KeyboardNavigation.AcceptsReturn", "True");

        var navItemsControls = document
            .Descendants(Presentation + "ItemsControl")
            .Where(element =>
                (string?)element.Attribute("ItemsSource") is "{Binding NavGroups}" or "{Binding Children}")
            .ToList();
        Assert.Equal(2, navItemsControls.Count);
        Assert.All(
            navItemsControls,
            itemsControl => Assert.Empty(
                itemsControl.Elements(Presentation + "ItemsControl.ItemContainerStyle")));
    }

    [Fact]
    public void CollapsedGroups_KeepHiddenLeavesOutOfKeyboardNavigation()
    {
        var appDocument = LoadProjectXaml("App.xaml");
        var expanderStyle = FindStyle(appDocument, "SidebarExpander");
        var header = expanderStyle
            .Descendants(Presentation + "ToggleButton")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "HeaderSite");

        Assert.Equal("True", (string?)header.Attribute("Focusable"));
        Assert.Equal("True", (string?)header.Attribute("KeyboardNavigation.IsTabStop"));
        // The header is a ToggleButton, so it too answers Space by default and Enter only with this.
        // Reaching a collapsed group and being unable to open it is the same dead end as not reaching it.
        Assert.Equal("True", (string?)header.Attribute("KeyboardNavigation.AcceptsReturn"));
        Assert.Equal(
            "{Binding Id, StringFormat={}{0}-header}",
            (string?)header.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal(
            "{Binding Label}",
            (string?)header.Attribute("AutomationProperties.Name"));

        var focusTrigger = header
            .Descendants(Presentation + "Trigger")
            .Single(trigger =>
                (string?)trigger.Attribute("Property") == "IsKeyboardFocused"
                && (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(
            focusTrigger.Elements(Presentation + "Setter"),
            setter =>
                (string?)setter.Attribute("TargetName") == "HeaderFocusBorder"
                && (string?)setter.Attribute("Property") == "BorderBrush"
                && (string?)setter.Attribute("Value") == "{DynamicResource Accent}");

        var contentPanel = expanderStyle
            .Descendants(Presentation + "Border")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ContentPanel");
        Assert.Equal(
            "{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}}",
            (string?)contentPanel.Attribute("IsEnabled"));

        var mainDocument = LoadProjectXaml("MainWindow.xaml");
        var liveExpander = mainDocument
            .Descendants(Presentation + "Expander")
            .Single(element =>
                (string?)element.Attribute("Style") == "{StaticResource SidebarExpander}");
        Assert.Equal("{Binding Id}", (string?)liveExpander.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("{Binding Label}", (string?)liveExpander.Attribute("AutomationProperties.Name"));
    }

    /// <summary>
    /// No hover state in the sidebar may paint itself with the SELECTION brush.
    /// </summary>
    /// <remarks>
    /// <see cref="LiveRows_ShareSelectedVisualTreatment"/> pins the two brushes on the
    /// <c>SidebarNavRow</c> style itself. It cannot see the group-header <c>Border</c>, which carries its
    /// own inline style, and that header hovered in <c>AccentSoft</c> — the very brush this window uses to
    /// mark the selected page. Two different meanings, one colour, in one control: moving the mouse down
    /// the sidebar made each group look momentarily like the open one.
    /// <para>Deliberately scoped to this window. Nine views elsewhere tint a hovered row with
    /// <c>AccentSoft</c> and none of them use it for selection, so there is nothing to confuse; the rule
    /// is not "AccentSoft is never a hover colour" but "not where it already means selected".</para>
    /// </remarks>
    [Fact]
    public void NoSidebarHover_BorrowsTheSelectionBrush()
    {
        var document = LoadProjectXaml("MainWindow.xaml");

        // Any trigger shape, including a MultiTrigger whose condition list mentions the property, so a
        // future rewrite into conditions cannot slip past this.
        var hoverTriggers = document
            .Descendants()
            .Where(element => element.Name.LocalName.EndsWith("Trigger", StringComparison.Ordinal))
            .Where(element =>
                (string?)element.Attribute("Property") == "IsMouseOver"
                || element.Descendants(Presentation + "Condition")
                    .Any(condition => (string?)condition.Attribute("Property") == "IsMouseOver"))
            .ToList();

        // Vacuity floor: three exist today (two row hovers and the keyboard-focus border's sibling).
        Assert.True(
            hoverTriggers.Count >= 3,
            $"only {hoverTriggers.Count} hover triggers were found in MainWindow.xaml — the trigger "
            + "selector has stopped matching, so this guard is reading nothing.");

        var borrowed = hoverTriggers
            .SelectMany(trigger => trigger.Descendants(Presentation + "Setter"))
            .Where(setter =>
                ((string?)setter.Attribute("Property"))?.EndsWith("Background", StringComparison.Ordinal)
                    == true
                && ((string?)setter.Attribute("Value"))?.Contains("AccentSoft", StringComparison.Ordinal)
                    == true)
            .Select(setter => (string?)setter.Attribute("Value") ?? "")
            .ToList();

        Assert.True(
            borrowed.Count == 0,
            "a sidebar hover state paints itself with AccentSoft, which is what this window uses to mark "
            + "the SELECTED page: hovering a group then looks identical to having opened it. Use RowHover, "
            + $"as the nav rows and DataGridRow already do. Found {borrowed.Count} occurrence(s): "
            + string.Join(", ", borrowed));
    }

    [Fact]
    public void LegacyTabItemStyle_IsNotKeptAsASecondSourceOfTruth()
    {
        var document = LoadProjectXaml("App.xaml");

        Assert.DoesNotContain(
            document.Descendants(Presentation + "Style"),
            style => (string?)style.Attribute(Xaml + "Key") == "SideNavTabItem");
    }

    private static void AssertNavButton(
        XElement button,
        string idBinding,
        string nameBinding,
        string statusBinding,
        string tagBinding,
        string clickHandler)
    {
        Assert.Equal(idBinding, (string?)button.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal(nameBinding, (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal(statusBinding, (string?)button.Attribute("AutomationProperties.ItemStatus"));
        Assert.Equal(tagBinding, (string?)button.Attribute("Tag"));
        Assert.Equal(clickHandler, (string?)button.Attribute("Click"));
        Assert.Equal("{StaticResource SidebarNavButton}", (string?)button.Attribute("Style"));
        Assert.Null(button.Attribute("Focusable"));
    }

    private static XElement FindStyle(XDocument document, string key) =>
        document
            .Descendants(Presentation + "Style")
            .Single(style => (string?)style.Attribute(Xaml + "Key") == key);

    private static XElement FindSelectedTrigger(XElement style) =>
        style
            .Descendants(Presentation + "DataTrigger")
            .Single(trigger =>
                (string?)trigger.Attribute("Binding") == "{Binding IsSelected}"
                && (string?)trigger.Attribute("Value") == "True");

    private static void AssertSetter(XElement owner, string property, string value) =>
        Assert.Contains(
            owner.Elements(Presentation + "Setter"),
            setter =>
                (string?)setter.Attribute("Property") == property
                && (string?)setter.Attribute("Value") == value);

    /// <summary>
    /// The appearance popup must take focus when it opens, give it back when it closes, and close on
    /// Escape.
    /// </summary>
    /// <remarks>
    /// A <c>Popup</c> is not a <c>Window</c>: it gets no Escape-to-close and no focus containment. Every
    /// preset card inside was already <c>Focusable</c>, a tab stop, and Enter-activatable — someone had
    /// clearly built for the keyboard — but opening the panel left focus behind on the chip, so none of
    /// it could be reached and there was no way out but the mouse. <c>Key.Escape</c> appeared nowhere in
    /// the app.
    /// <para>Asserted rather than demonstrated: the behaviour needs the app running, which happens on the
    /// other workstation. What is pinned here is the wiring that makes it possible, on both sides — the
    /// XAML hooks and the handlers they name. Half of it is useless alone: an <c>Opened</c> attribute
    /// with a handler that does not move focus reads as fixed and is not.</para>
    /// </remarks>
    [Fact]
    public void TheAppearancePopup_TakesFocusAndGivesItBack()
    {
        var document = LoadProjectXaml("MainWindow.xaml");
        var popup = document
            .Descendants(Presentation + "Popup")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ThemePopupHost");

        Assert.Equal("True", (string?)popup.Attribute("Focusable"));
        // Without Cycle, Tab walks out of the panel and behind it, which is worse than not focusing it.
        Assert.Equal("Cycle", (string?)popup.Attribute("KeyboardNavigation.TabNavigation"));
        Assert.Equal("ThemePopupHost_Opened", (string?)popup.Attribute("Opened"));
        Assert.Equal("ThemePopupHost_Closed", (string?)popup.Attribute("Closed"));

        // The handlers have to DO the thing. Comments are stripped first: this file explains the focus
        // moves in prose beside them, and a Contains against the raw text would be satisfied by the
        // explanation of a handler that had been emptied.
        var code = string.Join('\n', LoadProjectSource("MainWindow.xaml.cs")
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains("MoveFocus(new TraversalRequest(FocusNavigationDirection.First))", code,
                        StringComparison.Ordinal);
        Assert.Contains("ThemePopupHost_Closed(object sender, EventArgs e) => ThemeBtn.Focus()", code,
                        StringComparison.Ordinal);
        // Escape must be handled on the CHILD: with AllowsTransparency the popup has its own
        // PresentationSource, so a handler on the Popup element never sees the key.
        Assert.Contains("popup.PreviewKeyDown += ThemePopup_PreviewKeyDown", code, StringComparison.Ordinal);
        Assert.Contains("Key.Escape", code, StringComparison.Ordinal);
    }

    private static XDocument LoadProjectXaml(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "SysManager", fileName);
            if (File.Exists(candidate)) return XDocument.Load(candidate);
        }

        throw new FileNotFoundException($"Could not locate SysManager/{fileName} from the test output.");
    }

    private static string[] LoadProjectSource(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "SysManager", fileName);
            if (File.Exists(candidate)) return File.ReadAllLines(candidate);
        }

        throw new FileNotFoundException($"Could not locate SysManager/{fileName} from the test output.");
    }
}
