// SysManager · AppFixture
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace SysManager.UITests;

/// <summary>
/// xUnit collection fixture that launches the SysManager WPF app once per
/// test collection, attaches UI Automation (FlaUI + UIA3), and tears it
/// down at the end.
/// </summary>
public sealed class AppFixture : IDisposable
{
    public Application App { get; }
    public UIA3Automation Automation { get; } = new();
    public Window MainWindow { get; }
    private AutomationElement CurrentViewHost { get; }

    public AppFixture()
    {
        var exe = FindExecutable();
        App = Application.Launch(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = false
        });

        // A cold WPF app on a freshly-built headless CI runner can take a while to render
        // its first window, so allow generous time. On failure, report WHY (did the process
        // crash on launch, or is it just slow?) so a CI failure is diagnosable from the log.
        MainWindow = App.GetMainWindow(Automation, TimeSpan.FromSeconds(45))
            ?? throw new InvalidOperationException(
                "Main window did not appear in time. " +
                (App.HasExited
                    ? $"The app process EXITED with code {SafeExitCode()} — it crashed on launch rather than rendering."
                    : "The app process is still running but produced no main window within the timeout."));

        CurrentViewHost = Retry.WhileNull(
            () => FindUniqueDescendantById(MainWindow, "CurrentViewHost"),
            TimeSpan.FromSeconds(5)).Result
            ?? throw new InvalidOperationException("The current-view automation host was not exposed.");

        // Sidebar groups render as collapsed Expanders, so their child nav items aren't
        // realized in the UI Automation tree until expanded. Expand everything once up
        // front so tests that look up nav items directly (not via GoToTab) find them too.
        ExpandAllNavGroups();
    }

    /// <summary>
    /// Selects a nav entry by its AutomationId (e.g. "nav-network", "nav-logs").
    /// Works with both the old ListBox layout and the new grouped tree layout.
    /// </summary>
    public void GoToTab(string navId)
    {
        // Sidebar groups start collapsed, so child nav items aren't in the automation
        // tree until their group Expander is open. Drive the UI like a user: try to find
        // the item; if it isn't realized yet, expand every group and retry.
        var item = MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(navId));
        if (item is null)
        {
            ExpandAllNavGroups();
            item = Retry.WhileNull(() =>
                MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(navId)),
                TimeSpan.FromSeconds(5)).Result;
        }

        if (item is null)
            throw new InvalidOperationException($"Nav item '{navId}' not found.{DescribeNavTree()}");

        if (item.ControlType != ControlType.Button)
            throw new InvalidOperationException(
                $"Nav item '{navId}' is {item.ControlType}, not an invokable Button.");

        // Exercise the same UI Automation Invoke contract used by keyboard and
        // assistive-technology clients, without relying on screen coordinates.
        item.AsButton().Invoke();

        Thread.Sleep(250);
    }

    /// <summary>
    /// Expands every collapsible sidebar group so its child nav items are realized in the
    /// UI Automation tree. Groups render as Expanders; each is opened via its
    /// ExpandCollapse pattern when collapsed. Also clicks the header as a fallback for
    /// Expanders that don't expose the pattern.
    /// </summary>
    public void ExpandAllNavGroups()
    {
        foreach (var e in MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Group)))
        {
            try
            {
                var pattern = e.Patterns.ExpandCollapse.PatternOrDefault;
                if (pattern is not null && pattern.ExpandCollapseState.Value == ExpandCollapseState.Collapsed)
                    pattern.Expand();
                else if (pattern is null)
                    e.Click(); // header click toggles a templated Expander with no pattern
            }
            catch (Exception) { /* not an expandable group — skip */ }
        }
        Thread.Sleep(400);
    }

    /// <summary>
    /// Dumps the current automation tree (AutomationId / ControlType / Name) so a
    /// "nav item not found" failure in CI carries the real tree in its message/artifact,
    /// instead of needing an interactive FlaUI session to diagnose.
    /// </summary>
    private string DescribeNavTree()
    {
        try
        {
            var lines = MainWindow.FindAllDescendants()
                .Take(120)
                .Select(e =>
                {
                    var id = e.Properties.AutomationId.ValueOrDefault;
                    var name = e.Properties.Name.ValueOrDefault;
                    return $"  [{e.ControlType}] id='{id}' name='{name}'";
                });
            return "\nAutomation tree (first 120 elements):\n" + string.Join("\n", lines);
        }
        catch (Exception ex) { return $"\n(could not dump tree: {ex.Message})"; }
    }

    /// <summary>
    /// Wait up to <paramref name="timeoutSeconds"/> for any descendant whose
    /// Name contains <paramref name="text"/> (case-insensitive).
    /// </summary>
    public AutomationElement? WaitForText(string text, int timeoutSeconds = 5)
        => Retry.WhileNull(() =>
            MainWindow.FindAllDescendants()
                .FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.Name) &&
                    e.Name.Contains(text, StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(timeoutSeconds)).Result;

    /// <summary>Find a control by its AutomationId.</summary>
    public AutomationElement? FindById(string automationId) =>
        MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    /// <summary>
    /// Find a control in the currently rendered tab by its stable AutomationId.
    /// The retry lets the new view finish rendering after navigation on slow CI runners.
    /// </summary>
    public AutomationElement? FindByIdInCurrentTab(string automationId, int timeoutSeconds = 5) =>
        Retry.WhileNull(() =>
            FindUniqueDescendantById(CurrentViewHost, automationId),
            TimeSpan.FromSeconds(timeoutSeconds)).Result;

    /// <summary>Find a Button in the current tab by its stable AutomationId.</summary>
    public Button? FindButtonById(string automationId, int timeoutSeconds = 5)
    {
        var element = FindByIdInCurrentTab(automationId, timeoutSeconds);
        if (element is null) return null;
        if (element.ControlType != ControlType.Button)
        {
            throw new InvalidOperationException(
                $"Element '{automationId}' is {element.ControlType}, not a Button.");
        }

        return element.AsButton();
    }

    /// <summary>
    /// Find a Button by its exact accessible name. Reserved for accessible-name assertions
    /// and best-effort cleanup when the stable-id contract itself is under test.
    /// </summary>
    public Button? FindButtonByAccessibleName(string accessibleName, int timeoutSeconds = 1) =>
        Retry.WhileNull(() =>
            CurrentViewHost
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(button =>
                    string.Equals(button.Name, accessibleName, StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(timeoutSeconds)).Result?.AsButton();

    /// <summary>True when the current tab exposes a Button with the exact accessible name.</summary>
    public bool HasButtonWithName(string accessibleName, int timeoutSeconds = 1) =>
        FindButtonByAccessibleName(accessibleName, timeoutSeconds) is not null;

    /// <summary>
    /// Find the first Button whose accessible name STARTS WITH the given prefix.
    /// </summary>
    /// <remarks>
    /// For per-row buttons inside a DataGrid, which cannot carry a stable AutomationId — there is one
    /// per row, and an id must be unique — so their identity comes from a name built per item
    /// ("Mark or unmark this service: Print Spooler"). The exact-match overload above cannot find those
    /// without knowing which service the machine happens to list first, which would make the test depend
    /// on the machine rather than on the app.
    /// </remarks>
    public Button? FindButtonByAccessibleNamePrefix(string prefix, int timeoutSeconds = 5) =>
        Retry.WhileNull(() =>
            CurrentViewHost
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(button =>
                    button.Name?.StartsWith(prefix, StringComparison.Ordinal) is true),
            TimeSpan.FromSeconds(timeoutSeconds)).Result?.AsButton();

    private static AutomationElement? FindUniqueDescendantById(
        AutomationElement root,
        string automationId)
    {
        var matches = root.FindAllDescendants(cf => cf.ByAutomationId(automationId));
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"AutomationId '{automationId}' matched {matches.Length} elements; expected at most one.")
        };
    }

    /// <summary>
    /// True if any descendant's Name contains <paramref name="text"/>
    /// (case-insensitive), waiting up to <paramref name="timeoutSeconds"/>.
    /// A convenience over WaitForText for boolean presence assertions.
    /// </summary>
    public bool HasText(string text, int timeoutSeconds = 5)
        => WaitForText(text, timeoutSeconds) is not null;

    /// <summary>Wait for named content inside the currently rendered tab only.</summary>
    public AutomationElement? WaitForTextInCurrentTab(string text, int timeoutSeconds = 5) =>
        Retry.WhileNull(() =>
            CurrentViewHost.FindAllDescendants()
                .FirstOrDefault(element =>
                    !string.IsNullOrEmpty(element.Name)
                    && element.Name.Contains(text, StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(timeoutSeconds)).Result;

    public bool HasTextInCurrentTab(string text, int timeoutSeconds = 5)
        => WaitForTextInCurrentTab(text, timeoutSeconds) is not null;

    /// <summary>
    /// True when the current tab shows the shared "requires administrator"
    /// elevation banner (the not-elevated variant carries that phrase). Used to
    /// assert that privileged tabs surface the banner when the app is not elevated.
    /// </summary>
    public bool HasAdminBanner(int timeoutSeconds = 5)
        => WaitForText("requires administrator", timeoutSeconds) is not null;

    /// <summary>
    /// Count Button controls currently realized anywhere in the window. A crude
    /// but useful smoke signal that a tab rendered its action surface rather than
    /// an empty/crashed view.
    /// </summary>
    public int VisibleButtonCount()
        => MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).Length;

    /// <summary>Exit code of the launched app, or a marker if it can't be read.</summary>
    private string SafeExitCode()
    {
        try { return App.HasExited ? App.ExitCode.ToString() : "(still running)"; }
        catch (Exception ex) { return $"(unreadable: {ex.Message})"; }
    }

    private static string FindExecutable()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));

        // Search whichever configuration the app was actually built in. CI builds Release;
        // local dev often builds Debug — accept either rather than hardcoding one (the old
        // Debug-only lookup made every UI test fail under a Release build). The target-
        // framework folder is resolved dynamically so the path survives .NET version bumps.
        var searched = new List<string>();
        foreach (var config in new[] { "Release", "Debug" })
        {
            var binDir = Path.Combine(repoRoot, "SysManager", "bin", config);
            searched.Add(binDir);
            if (!Directory.Exists(binDir)) continue;

            var candidate = Directory
                .EnumerateDirectories(binDir, "net*-windows")
                .Select(tfm => Path.Combine(tfm, "SysManager.exe"))
                .FirstOrDefault(File.Exists);
            if (candidate is not null) return candidate;
        }

        throw new FileNotFoundException(
            $"Expected SysManager.exe under {string.Join(" or ", searched.Select(d => d + "\\net*-windows"))}. " +
            "Build SysManager (Debug or Release) before running UI tests.");
    }

    public void Dispose()
    {
        try
        {
            if (!App.HasExited) App.Close();
            App.Dispose();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"AppFixture: app teardown failed: {ex.Message}"); }
        try { Automation.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"AppFixture: automation teardown failed: {ex.Message}"); }
    }
}

[CollectionDefinition("App", DisableParallelization = true)]
public class AppCollection : ICollectionFixture<AppFixture> { }
