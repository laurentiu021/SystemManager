// SysManager · SettingsWatchdogViewBindingTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Xml.Linq;

namespace SysManager.Tests;

/// <summary>
/// Asserts that Settings Watchdog actually SHOWS what it watches.
/// </summary>
/// <remarks>
/// <para>`SettingsWatchdogViewModel.Watched` was populated in the constructor and bound by nothing, so
/// the tab rendered only settings that had already drifted. Before a drift the page held the heading,
/// the intro sentence — which names four of the eight watched settings as examples — and an empty
/// state. A user could not find out what the watchdog covers without reading the source.</para>
/// <para>A view-model test cannot catch that: `Watched` was correctly populated the whole time, and
/// every test of it would have passed. The absence existed only in the markup, which is why this
/// asserts against the shipped XAML — the same approach as
/// <c>AboutViewModelTests.AboutView_RendersTheRollbackButtonAndItsStatus</c> and the reachability
/// ratchet in <c>ArchitectureTests</c>, neither of which covers a collection.</para>
/// </remarks>
public class SettingsWatchdogViewBindingTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void TheViewBindsTheWatchedList()
    {
        var doc = XDocument.Load(ViewPath());

        var grids = doc.Descendants(Presentation + "DataGrid").ToList();
        Assert.NotEmpty(grids);   // else the assertions below would pass by finding nothing

        var watchedGrid = grids.FirstOrDefault(g =>
            (g.Attribute("ItemsSource")?.Value ?? "").Contains("Watched", StringComparison.Ordinal));
        Assert.NotNull(watchedGrid);

        // Unlike the drift grid, this one must NOT be gated on HasDrift — its entire purpose is to be
        // present BEFORE anything drifts, which is exactly the state that used to render nothing.
        var visibility = watchedGrid!.Attribute("Visibility")?.Value ?? "";
        Assert.DoesNotContain("HasDrift", visibility, StringComparison.Ordinal);

        // Every column the row type offers has to be rendered, or the list is a name-only list that
        // still cannot answer "what is set right now, and why is it watched".
        var columnBindings = watchedGrid
            .Descendants(Presentation + "DataGridTextColumn")
            .Select(c => c.Attribute("Binding")?.Value ?? "")
            .ToList();

        Assert.Contains(columnBindings, b => b.Contains("Name", StringComparison.Ordinal));
        Assert.Contains(columnBindings, b => b.Contains("Category", StringComparison.Ordinal));
        Assert.Contains(columnBindings, b => b.Contains("CurrentLabel", StringComparison.Ordinal));
        Assert.Contains(columnBindings, b => b.Contains("Description", StringComparison.Ordinal));
    }

    [Fact]
    public void ADriftedRowIsTintedInTheWatchedList()
    {
        // Both lists describe the same settings, so a setting must not read as settled in one while the
        // other flags it. The tint is what makes them agree on screen.
        var doc = XDocument.Load(ViewPath());

        var watchedGrid = doc.Descendants(Presentation + "DataGrid").First(g =>
            (g.Attribute("ItemsSource")?.Value ?? "").Contains("Watched", StringComparison.Ordinal));

        var rowStyles = watchedGrid
            .Descendants(Presentation + "DataGrid.RowStyle")
            .Elements(Presentation + "Style")
            .ToList();
        Assert.NotEmpty(rowStyles);

        Assert.All(rowStyles, style =>
        {
            // BasedOn, or the local style replaces the App.xaml DataGridRow style wholesale and the
            // app-wide row hover disappears — the defect this exact pattern already caused in LogsView.
            var basedOn = style.Attribute("BasedOn")?.Value ?? "";
            Assert.Contains("DataGridRow", basedOn, StringComparison.Ordinal);
        });

        var triggerBindings = rowStyles
            .Descendants(Presentation + "DataTrigger")
            .Select(t => t.Attribute("Binding")?.Value ?? "");
        Assert.Contains(triggerBindings, b => b.Contains("HasDrifted", StringComparison.Ordinal));
    }

    // Walks up from the test binaries to the app project — .xaml is not copied to the output.
    private static string ViewPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "SysManager", "Views", "SettingsWatchdogView.xaml");
        Assert.True(File.Exists(path), $"SettingsWatchdogView.xaml not found at {path}");
        return path;
    }
}
