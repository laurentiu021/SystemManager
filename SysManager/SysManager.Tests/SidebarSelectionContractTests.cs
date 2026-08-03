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
    public void LiveRows_ExposeSelectionToAutomationPeers()
    {
        var document = LoadProjectXaml("MainWindow.xaml");
        var singleRow = document
            .Descendants(Presentation + "ContentControl")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "SingleBd");

        Assert.Equal(
            "{Binding Children[0].Id}",
            (string?)singleRow.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal(
            "{Binding Children[0].Label}",
            (string?)singleRow.Attribute("AutomationProperties.Name"));
        Assert.Equal(
            "{Binding Children[0].SelectionStatus}",
            (string?)singleRow.Attribute("AutomationProperties.ItemStatus"));

        var outerItemsControl = document
            .Descendants(Presentation + "ItemsControl")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding NavGroups}");
        Assert.Empty(
            outerItemsControl.Elements(Presentation + "ItemsControl.ItemContainerStyle"));

        var groupedContainerStyle = document
            .Descendants(Presentation + "ItemsControl")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding Children}")
            .Elements(Presentation + "ItemsControl.ItemContainerStyle")
            .Single()
            .Element(Presentation + "Style")!;
        AssertSetter(
            groupedContainerStyle,
            "AutomationProperties.AutomationId",
            "{Binding Id}");
        AssertSetter(
            groupedContainerStyle,
            "AutomationProperties.Name",
            "{Binding Label}");
        AssertSetter(
            groupedContainerStyle,
            "AutomationProperties.ItemStatus",
            "{Binding SelectionStatus}");

        var groupedRow = document
            .Descendants(Presentation + "Border")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ChildBd");
        Assert.Null(groupedRow.Attribute("AutomationProperties.AutomationId"));
    }

    [Fact]
    public void LegacyTabItemStyle_IsNotKeptAsASecondSourceOfTruth()
    {
        var document = LoadProjectXaml("App.xaml");

        Assert.DoesNotContain(
            document.Descendants(Presentation + "Style"),
            style => (string?)style.Attribute(Xaml + "Key") == "SideNavTabItem");
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

    private static void AssertSetter(XElement trigger, string property, string value) =>
        Assert.Contains(
            trigger.Elements(Presentation + "Setter"),
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
