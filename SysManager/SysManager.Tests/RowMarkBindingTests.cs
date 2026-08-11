// SysManager · RowMarkBindingTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Xml.Linq;

namespace SysManager.Tests;

/// <summary>
/// Asserts that the row-marking feature is actually REACHABLE in the two tabs that ship it.
/// </summary>
/// <remarks>
/// <para>These exist because the feature was announced without its user-facing half. The original
/// commit added <c>IsHighlighted</c> to two models and <c>ToggleHighlight</c> to two view models,
/// touched the CHANGELOG, and touched no view — so for months the CHANGELOG told users they could
/// "toggle highlight on any log entry" while no control existed to do it and nothing rendered the
/// mark. Every unit test of the command passed the whole time, because a command with no binding is
/// still a perfectly working command.</para>
/// <para>That is the gap this file closes, and it is the same class of defect
/// <c>AboutViewModelTests.AboutView_RendersTheRollbackButtonAndItsStatus</c> guards for the rollback
/// button. Asserting against the shipped markup is the only place the missing half shows up: a
/// view-model test cannot see an absent binding, and the app compiles and runs perfectly without
/// one.</para>
/// </remarks>
public class RowMarkBindingTests
{
    [Theory]
    [InlineData("LogsView.xaml")]
    [InlineData("ServicesView.xaml")]
    public void TheMarkCommandIsBoundToAControl(string viewFile)
    {
        var xaml = File.ReadAllText(ViewPath(viewFile));

        // The command must be reachable from the row template. `DataContext.` is the qualifier the
        // codebase uses for per-row commands (the row is the DataContext inside a CellTemplate, so an
        // unqualified binding would silently resolve against the model and never fire).
        Assert.Contains("DataContext.ToggleHighlightCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"{Binding}\"", xaml, StringComparison.Ordinal);

        // …and the mark must be VISIBLE. A reachable command that renders nothing would be the same
        // bug with extra steps: the user clicks and the app appears to do nothing.
        Assert.Contains("IsHighlighted", xaml, StringComparison.Ordinal);

        // The escape hatch. Marks can sit on rows the current filter hides, so without a clear-all the
        // user has to un-filter and hunt each one.
        Assert.Contains("ClearHighlightsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("HighlightedCount", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LogsView.xaml")]
    [InlineData("ServicesView.xaml")]
    public void TheRowStyleInheritsTheAppWideOne(string viewFile)
    {
        // A local DataGrid.RowStyle without BasedOn REPLACES the implicit App.xaml style rather than
        // extending it, which silently drops the app-wide IsMouseOver -> RowHover tint. That already
        // happened: LogsView was the only view overriding RowStyle, and it was the only table in the
        // app where pointing at a row gave no feedback, against the project's own "hover feedback
        // everywhere the user can point" rule. Both views now need a row style for the mark tint, so
        // both need this guard — parsed as XML rather than grepped, so the assertion is about the
        // actual element and not a substring that happens to appear somewhere in the file.
        var doc = XDocument.Load(ViewPath(viewFile));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var rowStyles = doc.Descendants(p + "DataGrid.RowStyle")
            .Elements(p + "Style")
            .ToList();

        Assert.NotEmpty(rowStyles);   // else this test would pass by finding nothing
        Assert.All(rowStyles, style =>
        {
            var basedOn = style.Attribute("BasedOn")?.Value;
            Assert.False(string.IsNullOrWhiteSpace(basedOn),
                $"{viewFile}: a DataGrid.RowStyle without BasedOn replaces the implicit DataGridRow " +
                "style and loses the app-wide row hover.");
            Assert.Contains("DataGridRow", basedOn!, StringComparison.Ordinal);
        });
    }

    // Walks up from the test binaries to the app project — .xaml is not copied to the output.
    private static string ViewPath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);   // else the assertions above would silently test nothing
        var path = Path.Combine(dir!.FullName, "SysManager", "Views", fileName);
        Assert.True(File.Exists(path), $"{fileName} not found at {path}");
        return path;
    }
}
