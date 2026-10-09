// SysManager · FunctionalUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace SysManager.UITests;

/// <summary>
/// Functional UI tests: invoke SAFE, read-only actions and assert the
/// observable effect, not just that a control exists. Only non-destructive,
/// non-elevating operations are exercised here (scans, refreshes, list loads) —
/// nothing that changes system state, deletes files, or requires admin. Each
/// test drives the real app through UI Automation exactly as a user would.
/// </summary>
[Collection("App")]
public class FunctionalUiTests
{
    private readonly AppFixture _fx;
    public FunctionalUiTests(AppFixture fx) => _fx = fx;

    [Fact]
    public void Services_Refresh_PopulatesList()
    {
        _fx.GoToTab("nav-services");
        var refresh = _fx.FindButtonById("btn-services-refresh");
        Assert.NotNull(refresh);
        refresh!.Invoke();

        // A populated services list shows the "running / total" summary with a
        // non-zero total — every Windows box has dozens of services. Give the
        // background scan a few seconds to complete.
        var ok = FlaUI.Core.Tools.Retry.WhileFalse(
            () =>
            {
                var totalText = _fx.WaitForText("total", 1);
                // The summary reads e.g. "120 running / 250 total"; assert it isn't "0 total".
                return totalText is not null
                    && _fx.MainWindow.FindAllDescendants()
                        .Any(e => AppFixture.NameOf(e) is { } name
                                  && name.Contains("total", StringComparison.OrdinalIgnoreCase)
                                  && !name.TrimStart().StartsWith("0 ", StringComparison.Ordinal));
            },
            TimeSpan.FromSeconds(15)).Success;

        Assert.True(ok, "Services list did not populate with a non-zero total after Refresh.");
    }

    [Fact]
    public void Services_ClearMarksButton_AppearsOnlyOnceSomethingIsMarked()
    {
        // The mark feature was announced in 0.40.0 and shipped with no UI at all, so this drives it as a
        // user would: with nothing marked the "Clear marks" button must be ABSENT — a permanently
        // visible no-op button is the same class of defect the fix removes — and marking a row must make
        // it appear, which also proves the per-row flag button is reachable and actually toggles.
        _fx.GoToTab("nav-services");
        Assert.Null(_fx.FindButtonById("btn-services-clear-marks", timeoutSeconds: 1));

        // Wait for the list to populate, then click the first row's mark button. Per-row buttons cannot
        // carry a stable AutomationId (one per row, and ids must be unique), so each is named per
        // service — "Mark or unmark this service: Print Spooler" — and is matched on the prefix. Matching
        // the full name would make the test depend on which service this machine lists first.
        Assert.NotNull(_fx.WaitForText("total", 15));
        var mark = _fx.FindButtonByAccessibleNamePrefix("Mark or unmark this service", timeoutSeconds: 15);

        Assert.NotNull(mark);
        mark!.Invoke();

        var clear = FlaUI.Core.Tools.Retry.WhileNull(
            () => _fx.FindButtonById("btn-services-clear-marks", timeoutSeconds: 1),
            TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(clear);

        // …and clearing puts it back out of sight, so the button never lingers claiming marks that no
        // longer exist.
        clear!.Invoke();
        var gone = FlaUI.Core.Tools.Retry.WhileTrue(
            () => _fx.FindButtonById("btn-services-clear-marks", timeoutSeconds: 1) is not null,
            TimeSpan.FromSeconds(10)).Success;
        Assert.True(gone, "\"Clear marks\" stayed visible after every mark was cleared.");
    }

    /// <summary>
    /// F5 rebuilds the Services list, and keyboard focus comes back to the control it was on (#2609).
    /// </summary>
    /// <remarks>
    /// The refresh replaces every row, and the focused button went with its old row. WPF then put focus on the
    /// window, so someone using the keyboard had to Tab back in from the sidebar after every refresh. The mark button
    /// is the one focused here because its name carries the service, so the name of the element focused after F5
    /// says whether focus came back to the same row, and because it stays enabled while the list reloads. The new
    /// button has a runtime id of its own, which is what tells "focus came back" from "F5 rebuilt nothing".
    /// </remarks>
    [Fact]
    public void Services_F5_PutsKeyboardFocusBackOnTheSameRow()
    {
        _fx.GoToTab("nav-services");
        Assert.NotNull(_fx.WaitForText("total", 15));
        var mark = _fx.FindButtonByAccessibleNamePrefix("Mark or unmark this service", timeoutSeconds: 15);
        Assert.NotNull(mark);
        var name = AppFixture.NameOf(mark!);
        var before = RuntimeIdOf(mark!);
        Assert.NotNull(name);
        Assert.NotNull(before);

        _fx.MainWindow.SetForeground();
        mark!.Focus();
        var focused = FlaUI.Core.Tools.Retry.WhileFalse(
            () => FocusedElement() is { } f && AppFixture.NameOf(f) == name,
            TimeSpan.FromSeconds(5)).Success;
        Assert.True(focused, $"focus did not reach \"{name}\" before F5, so the test cannot say where F5 leaves it");

        Keyboard.Type(VirtualKeyShort.F5);

        AutomationElement? after = null;
        var back = FlaUI.Core.Tools.Retry.WhileFalse(
            () => (after = FocusedElement()) is { } f
                  && AppFixture.NameOf(f) == name
                  && RuntimeIdOf(f) is { } id && !id.SequenceEqual(before!),
            TimeSpan.FromSeconds(20)).Success;
        Assert.True(back, after is not null && RuntimeIdOf(after) is { } last && last.SequenceEqual(before!)
            ? "focus is still on the button focused before F5, so F5 did not rebuild the Services list"
            : $"after F5 rebuilt the list, focus was on {Describe(after)}, not back on \"{name}\"");
    }

    /// <summary>The element with keyboard focus, or null while focus is between two elements.</summary>
    private AutomationElement? FocusedElement()
    {
        try
        {
            return _fx.Automation.FocusedElement();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
        {
            return null;
        }
    }

    /// <summary>The element's runtime id, or null once it has gone.</summary>
    private static int[]? RuntimeIdOf(AutomationElement element)
    {
        try
        {
            return element.Properties.RuntimeId.ValueOrDefault;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or PropertyNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>What a failure message says the focused element was.</summary>
    private static string Describe(AutomationElement? element)
    {
        if (element is null) return "nothing";
        try
        {
            return $"the {element.ControlType} \"{AppFixture.NameOf(element)}\"";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or PropertyNotSupportedException)
        {
            return "an element that has gone";
        }
    }

    [Fact]
    public void Logs_Refresh_LoadsEventsOrReportsState()
    {
        _fx.GoToTab("nav-logs");
        var refresh = _fx.FindButtonById("btn-logs-refresh");
        Assert.NotNull(refresh);
        refresh!.Invoke();

        // After a refresh the status bar resolves to a terminal state: either
        // "Loaded N events …" on success, or a clear access/error message. Any of
        // these proves the command ran to completion rather than hanging.
        var resolved = FlaUI.Core.Tools.Retry.WhileNull(
            () => _fx.WaitForText("Loaded", 1)
                  ?? _fx.WaitForText("Access denied", 1)
                  ?? _fx.WaitForText("Event log error", 1),
            TimeSpan.FromSeconds(20)).Result;

        Assert.NotNull(resolved);
    }

    /// <summary>
    /// The driver scan reaches a terminal state: a "&lt;N&gt; drivers found" summary, or the message the
    /// view model sets when the scan failed or its output could not be read.
    /// </summary>
    /// <remarks>
    /// The 25 s bound this used to carry was too tight for what the scan actually does: it spawns
    /// <c>pwsh</c> and queries <c>Win32_PnPSignedDriver</c>, one of WMI's slowest classes, on a shared CI
    /// runner with a cold PowerShell start. It failed twice on 2026-08-17 at 25 s and 27 s — at the wall,
    /// not on a wrong result — and passed on the runs in between. Two changes make it honest rather than
    /// lucky: the budget matches the real cost, and a timeout now reports whether the scan was STILL
    /// RUNNING (slow environment, not a defect) or had gone quiet (a real hang), so the next person is not
    /// left guessing from <c>Assert.NotNull() Failure: Value is null</c>.
    /// </remarks>
    [Fact]
    public void Drivers_List_ProducesCountOrDone()
    {
        _fx.GoToTab("nav-drivers");
        var list = _fx.FindButtonById("btn-drivers-list");
        Assert.NotNull(list);
        list!.Invoke();

        // Either terminal message proves the command ran to completion rather than dying silently.
        // Deliberately NOT also accepting a bare "Done": WaitForText searches the whole window, so that
        // would match the status line of any other tab and pass without the scan having finished.
        var done = FlaUI.Core.Tools.Retry.WhileNull(
            () => _fx.WaitForText("drivers found", 1)
                  ?? _fx.WaitForText("Could not read the installed drivers", 1),
            TimeSpan.FromSeconds(90)).Result;

        // Distinguish "slower than the budget" from "never finished" — the progress text is only on
        // screen while the scan is in flight.
        var stillScanning = _fx.HasText("Scanning installed drivers", 1);
        Assert.True(done is not null,
            stillScanning
                ? "The driver scan was still running after 90 s (Win32_PnPSignedDriver via pwsh on a "
                  + "loaded runner). The app is not broken; the budget is too small for this environment."
                : "The driver scan produced no terminal state and is no longer reporting progress — "
                  + "it finished without setting a summary, or failed silently.");
    }

    [Fact]
    public void DiskAnalyzer_ShowsReadOnlyEmptyState_BeforeScan()
    {
        // Read-only tab: before analyzing anything it must show its neutral
        // empty-state guidance, never a stale or error state.
        _fx.GoToTab("nav-disk-analyzer");
        Assert.True(
            _fx.HasText("pick a folder") || _fx.HasText("No results"),
            "Disk Analyzer did not show its pre-scan empty-state message.");
    }

    [Fact]
    public void ProcessManager_AutoPopulates_ProcessList()
    {
        _fx.GoToTab("nav-processes");
        // Process Manager auto-refreshes on a 1s loop; the summary resolves to
        // "<N> processes · <size> total memory" once the scan completes. Match the
        // "total memory" tail so this doesn't trivially pass on the banner text
        // ("Ending system processes requires administrator privileges.").
        var populated = FlaUI.Core.Tools.Retry.WhileNull(
            () => _fx.WaitForText("total memory", 1),
            TimeSpan.FromSeconds(12)).Result;
        Assert.NotNull(populated);
    }

    [Fact]
    public void RapidTabSwitching_DoesNotCrash_AndDashboardRecovers()
    {
        // Stress the navigation: hop across heavy tabs quickly, then confirm the
        // app is still alive and the Dashboard still renders its score.
        foreach (var id in new[] {
            "nav-processes", "nav-logs", "nav-services", "nav-system-health",
            "nav-disk-analyzer", "nav-dns-hosts", "nav-dashboard" })
        {
            _fx.GoToTab(id);
        }
        Assert.False(_fx.App.HasExited, "App exited during rapid tab switching.");
        Assert.True(_fx.HasText("Dashboard"), "Dashboard did not recover after rapid tab switching.");
    }
}
