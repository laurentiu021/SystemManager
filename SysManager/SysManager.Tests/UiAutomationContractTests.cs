// SysManager · UiAutomationContractTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SysManager.Tests;

public partial class UiAutomationContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly IReadOnlyDictionary<string, string> DescriptiveAccessibleNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["btn-cleanup-clean-temp"] = "Clean temporary files",
            ["btn-cleanup-empty-recycle-bin"] = "Empty the Recycle Bin",
            ["btn-cleanup-sfc"] = "Run SFC system file check",
            ["btn-cleanup-dism"] = "Run DISM RestoreHealth",
            ["btn-cleanup-cancel"] = "Cancel the running operation",
            ["btn-system-health-scan"] = "Scan system health",
            ["btn-system-health-smart"] = "Run SMART disk health check",
            ["btn-system-health-memtest"] = "Schedule a memory test on next reboot",
            ["btn-logs-refresh"] = "Refresh logs",
            ["btn-logs-export-csv"] = "Export logs to CSV",
            ["btn-services-refresh"] = "Refresh services",
            ["btn-ping-start"] = "Start ping monitoring",
            ["btn-ping-stop"] = "Stop ping monitoring",
            ["btn-ping-clear"] = "Clear ping history",
            ["btn-dashboard-scan-system"] = "Scan system",
            ["btn-drivers-list"] = "List drivers",
            ["btn-uninstaller-uninstall-selected"] = "Uninstall selected applications",
            ["btn-windows-update-install-module"] = "Install PSWindowsUpdate for update history",
            ["btn-windows-update-list"] = "List available Windows updates",
            ["btn-windows-update-history"] = "Show Windows Update history",
            ["btn-windows-update-pending-reboot"] = "Check for a pending reboot",
            ["btn-windows-update-install-selected"] = "Install selected Windows updates",
            ["btn-app-updates-scan"] = "Scan for updates",
            ["btn-system-health-memory-errors"] = "Check memory errors",
            ["btn-logs-open-event-viewer"] = "Open Event Viewer",
            // Renamed for #1647: this button sits beside "Open Event Viewer" on a tab titled
            // after the Windows Event Log, but opens SysManager's own diagnostic folder. The
            // accessible name now says whose logs, so a screen-reader user is not misled either.
            ["btn-logs-open-folder"] = "Open SysManager's own log folder",
            ["btn-ping-add-target"] = "Add target"
        };

    [Fact]
    public void AssertedButtons_HaveUniqueStableAutomationIds()
    {
        var solutionDirectory = FindSolutionDirectory();
        var uiTestSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    Path.Combine(solutionDirectory.FullName, "SysManager.UITests"),
                    "*.cs",
                    SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));

        var referencedIds = FindButtonByIdCall()
            .Matches(uiTestSource)
            .Select(match => match.Groups["id"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.DoesNotContain("FindButton(", uiTestSource, StringComparison.Ordinal);

        var sourceXaml = EnumerateSourceXaml(solutionDirectory)
            .Select(XDocument.Load)
            .ToArray();
        var actionElements = sourceXaml
            .SelectMany(document => document.Descendants())
            .Select(element => new
            {
                Element = element,
                Id = (string?)element.Attribute("AutomationProperties.AutomationId")
            })
            .Where(item => item.Id?.StartsWith("btn-", StringComparison.Ordinal) is true)
            .ToArray();
        var buttonIds = actionElements
            .Select(item => item.Id!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(referencedIds);
        Assert.Equal(actionElements.Length, DescriptiveAccessibleNames.Count);
        Assert.All(actionElements, item =>
        {
            Assert.Equal(Presentation + "Button", item.Element.Name);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    (string?)item.Element.Attribute("AutomationProperties.Name")),
                $"Button '{item.Id}' has no explicit accessible name.");
        });
        foreach (var (id, expectedName) in DescriptiveAccessibleNames)
        {
            var action = Assert.Single(actionElements, item => item.Id == id);
            Assert.Equal(
                expectedName,
                (string?)action.Element.Attribute("AutomationProperties.Name"));
        }
        Assert.Equal(buttonIds.Distinct(StringComparer.Ordinal), buttonIds);
        Assert.Equal(referencedIds, buttonIds);

        var currentViewHost = Assert.Single(
            sourceXaml.SelectMany(document => document.Descendants()),
            element =>
                (string?)element.Attribute("AutomationProperties.AutomationId")
                == "CurrentViewHost");
        Assert.Equal(Presentation + "UserControl", currentViewHost.Name);
        Assert.Equal("{Binding SelectedNav.View}", (string?)currentViewHost.Attribute("Content"));
    }

    private static DirectoryInfo FindSolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "SysManager.UITests"))
                && Directory.Exists(Path.Combine(directory.FullName, "SysManager", "Views")))
            {
                return directory;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the SysManager solution directory from the test output.");
    }

    private static IEnumerable<string> EnumerateSourceXaml(DirectoryInfo solutionDirectory)
    {
        var projectDirectory = Path.Combine(solutionDirectory.FullName, "SysManager");
        return Directory
            .EnumerateFiles(projectDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(projectDirectory, path)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment =>
                    segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)));
    }

    [GeneratedRegex("FindButtonById\\s*\\(\\s*\"(?<id>btn-[a-z0-9-]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex FindButtonByIdCall();
}
