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
        Assert.Equal(4, liveTexts.Count);
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
}
