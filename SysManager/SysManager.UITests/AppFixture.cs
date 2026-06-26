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

        item.Click();
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

    /// <summary>
    /// Find a Button by its exact visible content text, retrying up to
    /// <paramref name="timeoutSeconds"/>. The retry matters on a slow CI runner: a tab's
    /// buttons aren't realized in the automation tree the instant navigation happens, so a
    /// single snapshot (as this used to do) returned null before the content rendered.
    /// </summary>
    public Button? FindButton(string text, int timeoutSeconds = 5) =>
        Retry.WhileNull(() =>
            MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => string.Equals(b.Name, text, StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(timeoutSeconds)).Result?.AsButton();

    /// <summary>Find a control by its AutomationId.</summary>
    public AutomationElement? FindById(string automationId) =>
        MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

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
