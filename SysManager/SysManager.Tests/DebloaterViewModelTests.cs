// SysManager · DebloaterViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DebloaterViewModel"/> empty-state copy — the centered empty
/// state must switch from "press Refresh" (never scanned) to "none found" (scanned,
/// zero results) so it never contradicts the status bar. Constructed with a mocked
/// <see cref="IPowerShellRunner"/> so no real PowerShell runs.
/// </summary>
// Serialized: RemoveSelected_RunspaceFault swaps the static DialogService.Instance, which is
// process-wide shared state. Without this attribute the class ran in PARALLEL with the 24 classes
// that ARE in the collection (parallelizeTestCollections is true), so two tests could interleave
// their save/restore and leave a foreign substitute installed in the singleton for the rest of the
// run — a confirmation gate answering with another test's canned answer.
[Collection("ProcessWideStatics")]
public class DebloaterViewModelTests
{
    /// <summary>
    /// A view-model with an unconfigured runner, its constructor init settled.
    /// </summary>
    /// <remarks>
    /// Settled for the same reason as the overload below (#2201). It matters here even though these tests
    /// assert no <c>StatusMessage</c>: the init sets <c>HasScanned = true</c>, so
    /// <see cref="EmptyState_BeforeScan_PromptsRefresh"/> was asserting <c>HasScanned</c> is FALSE as a
    /// precondition it did not control — true only while the fire-and-forget init had not landed yet. Both
    /// empty-state tests now set the flag they are about, which is what they were always testing: the copy
    /// switches on <c>HasScanned</c>, not on how fast a background load happens to run.
    /// </remarks>
    private static DebloaterViewModel NewVm()
    {
        // Returns an EMPTY collection explicitly. An unconfigured substitute hands back a null
        // Collection<PSObject>, which made DebloaterService.ParsePackages throw NullReferenceException
        // out of the init — invisible until this factory started settling it, because RunInitAsync does not
        // catch that type and nothing in production observes the task. Filed separately; a test fixture
        // should state what the runner returns rather than lean on an auto-value either way.
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Collection<PSObject>()));

        var vm = new DebloaterViewModel(new DebloaterService(runner), NoRestorePoint());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    /// <summary>
    /// A seam that answers "no point was created" — the common case on a consumer machine, where
    /// System Restore is off or Windows has already used its 24-hour allowance.
    /// </summary>
    private static ISessionRestorePoint NoRestorePoint()
    {
        var rp = Substitute.For<ISessionRestorePoint>();
        rp.EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        return rp;
    }

    private static StoreApp Removable(string name) => new()
    {
        Name = name,
        DisplayName = name,
        PackageFullName = $"{name}_1.0.0.0_x64__8wekyb3d8bbwe",
        PackageFamilyName = $"{name}_8wekyb3d8bbwe",
        Publisher = "CN=Test",
        Version = "1.0.0.0",
        IsProtected = false,
        IsSelected = true,
    };

    [Fact]
    public void EmptyState_BeforeScan_PromptsRefresh()
    {
        var vm = NewVm();

        // Set explicitly rather than relied on. The init sets this true, so asserting it was false meant
        // asserting the background load had not landed yet — which is timing, not behaviour. The sibling
        // test below has always set it the other way for exactly this reason.
        vm.HasScanned = false;

        Assert.Equal("No apps loaded", vm.EmptyTitle);
        Assert.Contains("Refresh", vm.EmptyMessage);
    }

    [Fact]
    public void EmptyState_AfterScan_SwitchesToNoneFound()
    {
        var vm = NewVm();

        // Simulate a completed scan (the flag the RefreshAsync path sets on completion).
        vm.HasScanned = true;

        Assert.Equal("No Store apps found", vm.EmptyTitle);
        Assert.DoesNotContain("Refresh", vm.EmptyMessage);
    }

    // ---------- removal-batch resilience (regression P2 #44) ----------

    [Fact]
    public async Task RemoveSelected_RunspaceFault_FailsRowsAndCompletes_NoEscape()
    {
        // Regression (P2 #44): RemoveAsync runs PowerShell; a runspace-level fault
        // (InvalidOperationException — e.g. PSInvalidOperationException from a failed
        // runspace open) is NOT the RuntimeException the service catches. Before the fix
        // the whole RemoveSelected loop had a single OperationCanceledException catch, so
        // such a fault escaped to the global dispatcher MessageBox, aborted the batch, and
        // left later rows frozen at "Removing…". Now each row is guarded: the command must
        // complete without throwing and mark BOTH selected apps "Failed".
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns<Task<Collection<PSObject>>>(_ => throw new InvalidOperationException("runspace is not open"));

        // DialogAnswer(true) — the user clicks "Yes". The shared helper replaces a hand-rolled
        // save/restore: it is what the other 24 classes use, and its restore is equally
        // exception-safe.
        using var dialog = new DialogAnswer(confirm: true);

        var vm = NewVm(NoRestorePoint(), runner);
        var a = Removable("Contoso.AppA");
        var b = Removable("Contoso.AppB");
        vm.Apps.Add(a);
        vm.Apps.Add(b);

        var ex = await Record.ExceptionAsync(() => vm.RemoveSelectedCommand.ExecuteAsync(null));

        Assert.Null(ex);                       // must not fault the command
        Assert.Equal("Failed", a.Status);      // first row failed, not frozen at "Removing…"
        Assert.Equal("Failed", b.Status);      // batch continued to the second row
    }

    // ---------- session restore point (#1500) ----------
    // The snapshot has to happen BEFORE the first removal and must never be claimed unless Windows
    // actually created one. Debloater is the tab where over-claiming would do the most damage: a
    // restore point does NOT bring removed Appx packages back, so the Store reinstall has to lead
    // and the point can only be described as covering the rest of the system.

    private static ISessionRestorePoint RestorePointTaken()
    {
        var rp = Substitute.For<ISessionRestorePoint>();
        rp.EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        return rp;
    }

    /// <summary>A runner whose Remove-AppxPackage script reports the service's success sentinel.</summary>
    private static IPowerShellRunner RunnerThatRemovesSuccessfully()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Collection<PSObject> { new("__SM_RM_OK__") }));
        return runner;
    }

    /// <summary>
    /// Builds the view-model and SETTLES its constructor init before returning it.
    /// </summary>
    /// <remarks>
    /// The constructor fires <c>RefreshAsync</c> and forgets it, and that init does two things no test
    /// here survives landing late (#2201):
    /// <list type="number">
    /// <item>it writes <c>StatusMessage</c> — "Reading installed Store apps…" — which then overwrites
    /// whatever the command under test reported, so the assertion fails on a string neither the test nor
    /// the command produced. That is the exact failure that reddened an unrelated PR on #2199.</item>
    /// <item>it calls <c>Apps.ReplaceWith(...)</c>, which DISCARDS the fixture. Every test in this file adds
    /// its apps immediately after constructing, so a late init leaves the command acting on an empty list —
    /// and a test that then asserts "nothing was removed" passes for the wrong reason entirely.</item>
    /// </list>
    /// <para>The second is why this is a factory rather than an await added to the three tests that assert
    /// on <c>StatusMessage</c>: all six construction sites add to <c>Apps</c>, so all six are exposed,
    /// whatever they go on to assert.</para>
    /// <para>Settled synchronously, mirroring <c>AppBlockerViewModelTests.NewVm</c> and the four other
    /// factories in this suite that do the same, so the sync tests here need no signature change.</para>
    /// </remarks>
    private static DebloaterViewModel NewVm(
        ISessionRestorePoint restorePoint, IPowerShellRunner? runner = null)
    {
        var vm = new DebloaterViewModel(
            new DebloaterService(runner ?? RunnerThatRemovesSuccessfully()), restorePoint);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public async Task RemoveSelected_TakesTheRestorePointBeforeRemovingAnything()
    {
        var restorePoint = RestorePointTaken();
        using var dialog = new DialogAnswer(confirm: true);

        var vm = NewVm(restorePoint);
        vm.Apps.Add(Removable("Contoso.AppA"));

        await vm.RemoveSelectedCommand.ExecuteAsync(null);

        await restorePoint.Received(1).EnsureAsync("SysManager Debloater", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveSelected_WhenTheUserDeclines_TakesNoRestorePoint()
    {
        // Declining is not a system change, so it must not spend the one point Windows allows per day.
        var restorePoint = RestorePointTaken();
        using var dialog = new DialogAnswer(confirm: false);

        var vm = NewVm(restorePoint);
        vm.Apps.Add(Removable("Contoso.AppA"));

        await vm.RemoveSelectedCommand.ExecuteAsync(null);

        await restorePoint.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Contains("cancelled", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveSelected_WithNothingSelected_TakesNoRestorePoint()
    {
        var restorePoint = RestorePointTaken();
        var vm = NewVm(restorePoint);
        var app = Removable("Contoso.AppA");
        app.IsSelected = false;
        vm.Apps.Add(app);

        await vm.RemoveSelectedCommand.ExecuteAsync(null);

        await restorePoint.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveSelected_WhenAPointWasCreated_LeadsWithTheStoreAndScopesThePoint()
    {
        using var dialog = new DialogAnswer(confirm: true);

        var vm = NewVm(RestorePointTaken());
        vm.Apps.Add(Removable("Contoso.AppA"));

        await vm.RemoveSelectedCommand.ExecuteAsync(null);

        // The true undo first...
        Assert.Contains("Microsoft Store", vm.StatusMessage, StringComparison.Ordinal);
        // ...and the point described for what it actually covers, never as a way to get the apps back.
        Assert.Contains("not the apps themselves", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(vm.StatusMessage.IndexOf("Microsoft Store", StringComparison.Ordinal)
                    < vm.StatusMessage.IndexOf("restore point", StringComparison.Ordinal),
            $"the Store reinstall must lead, since it is the only real undo: \"{vm.StatusMessage}\"");
    }

    [Fact]
    public async Task RemoveSelected_WhenNoPointWasCreated_ClaimsNoRestorePoint()
    {
        // System Restore is off on many consumer machines and Windows rate-limits it to about one point
        // per 24h, so "no point" is the common case — and a safety net the user does not have is worse
        // than none at all, because she would press the button on the strength of it.
        using var dialog = new DialogAnswer(confirm: true);

        var vm = NewVm(NoRestorePoint());
        vm.Apps.Add(Removable("Contoso.AppA"));

        await vm.RemoveSelectedCommand.ExecuteAsync(null);

        Assert.DoesNotContain("restore point", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        // The removal itself still reported normally.
        Assert.Contains("Removed 1 app", vm.StatusMessage, StringComparison.Ordinal);
    }
}
