// SysManager · UninstallerViewModelLeftoverTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// The Uninstaller's "Left behind" card (#1527): it appears after an uninstall that removed something, offers only
/// what the search allows, asks before removing anything, and in a session that runs as administrator offers what an
/// earlier one could not remove. The leftover service is a substitute and the uninstall list a disposable HKCU
/// subkey, so no uninstaller runs and nothing is searched for, recycled or deleted.
/// </summary>
/// <remarks>In the serialized collection because these tests answer the tab's confirmation dialogs.</remarks>
[Collection("ProcessWideStatics")]
public sealed class UninstallerViewModelLeftoverTests : IDisposable
{
    // A real, trusted executable, so the path checks in UninstallLocalAsync pass. The runner is substituted, so it is
    // never launched — the same stand-in UninstallerRemovalCheckTests uses.
    private static readonly string Uninstaller = $"\"{Path.Combine(Environment.SystemDirectory, "where.exe")}\"";

    private readonly string _rootName = @"Software\SysManagerTests\LeftoverList_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly IPowerShellRunner _runner = Substitute.For<IPowerShellRunner>();
    private readonly ILeftoverService _leftovers = Substitute.For<ILeftoverService>();

    public UninstallerViewModelLeftoverTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
        _runner.RunProcessWithShellAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);
        _leftovers.LoadPending().Returns([]);
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); }
        catch (System.Security.SecurityException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private UninstallerViewModel Vm(bool elevated = false) =>
        new(new UninstallerService(_runner, () => false, machineRoot: _root, userRoot: _root), _leftovers, () => elevated)
        {
            IsElevated = elevated,
        };

    private static InstalledApp App(string name, string installLocation = "") =>
        new() { Name = name, Id = $@"ARP\{name}", Publisher = "Acme", UninstallString = Uninstaller, InstallLocation = installLocation };

    private static LeftoverItem Item(string path, LeftoverConfidence confidence = LeftoverConfidence.Certain,
                                     long size = 1024, bool needsAdmin = false) =>
        new()
        {
            Location = path,
            Kind = LeftoverKind.Folder,
            Confidence = confidence,
            SizeBytes = size,
            NeedsAdministrator = needsAdmin,
            IsSelected = confidence == LeftoverConfidence.Certain && !needsAdmin,
        };

    private static LeftoverGroup Group(string app, params LeftoverItem[] items) =>
        new() { AppName = app, Publisher = "Acme", Items = [.. items] };

    private void Finds(params LeftoverGroup[] groups)
    {
        var queue = new Queue<LeftoverGroup>(groups);
        _leftovers.FindAsync(Arg.Any<UninstallProbe>(), Arg.Any<IReadOnlyCollection<UninstallProbe>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(queue.Count > 0 ? queue.Dequeue() : new LeftoverGroup { AppName = "?" }));
    }

    /// <summary>Lists <paramref name="apps"/>, ticks them all, and uninstalls them through a launcher that returns 0.</summary>
    private static async Task UninstallAsync(UninstallerViewModel vm, params InstalledApp[] apps)
    {
        foreach (var app in apps) vm.AllApps.Add(app);
        vm.FilterText = "x";   // repopulates FilteredApps through the public filter path
        vm.FilterText = "";
        foreach (var row in vm.FilteredApps) row.IsSelected = true;

        using (new DialogAnswer(confirm: true))
            await vm.UninstallSelectedCommand.ExecuteAsync(null);
    }

    // ── After an uninstall ────────────────────────────────────────────────

    [Fact]
    public async Task TheSearch_StartsFromWhatWasKnownBeforeTheUninstallerRan()
    {
        var app = App("Acme Notes", @"C:\Program Files\Acme Notes");
        // Whatever happens to the row while the uninstaller runs, the search is given the state before it.
        _runner.RunProcessWithShellAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { app.InstallLocation = ""; return Task.FromResult(0); });
        Finds(Group("Acme Notes"));
        var vm = Vm();

        await UninstallAsync(vm, app, App("Other App"));

        await _leftovers.Received(1).FindAsync(
            Arg.Is<UninstallProbe>(p => p.Name == "Acme Notes" && p.InstallLocation == @"C:\Program Files\Acme Notes"
                                        && p.UninstallCommand == Uninstaller),
            Arg.Any<IReadOnlyCollection<UninstallProbe>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhatWasFound_IsShown_Remembered_AndTheBatchSummaryStaysOnTheStatusLine()
    {
        var found = Group("Acme Notes", Item(@"C:\Program Files\Acme Notes", size: 2048),
                          Item(@"C:\Users\me\AppData\Roaming\Acme Notes", LeftoverConfidence.Probably));
        Finds(found);
        var vm = Vm();

        await UninstallAsync(vm, App("Acme Notes"));

        Assert.True(vm.ShowLeftovers);
        Assert.True(vm.HasLeftoverItems);
        Assert.Same(found, Assert.Single(vm.LeftoverGroups));
        Assert.StartsWith("1 app was uninstalled. These were still on the PC afterwards. Ticked items are certain",
            vm.LeftoverIntro, StringComparison.Ordinal);
        Assert.Equal("1 ticked · 2.0 KB · goes to the Recycle Bin, so you can put it back", vm.LeftoverSelection);
        Assert.Equal("Completed 1/1 uninstalls.", vm.StatusMessage);
        _leftovers.Received(1).RememberPending(Arg.Is<IReadOnlyList<LeftoverGroup>>(g => g.Count == 1 && g[0] == found));
    }

    [Fact]
    public async Task AnUninstallThatRemovedNothing_SearchesForNothing()
    {
        _runner.RunProcessWithShellAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1);
        var vm = Vm();

        await UninstallAsync(vm, App("Acme Notes"));

        await _leftovers.DidNotReceiveWithAnyArgs().FindAsync(default!, default!, default);
        Assert.False(vm.ShowLeftovers);
    }

    [Fact]
    public async Task NothingFound_SaysNothingWasLeftBehind()
    {
        Finds(Group("Acme Notes"));
        var vm = Vm();

        await UninstallAsync(vm, App("Acme Notes"));

        Assert.True(vm.ShowLeftovers);
        Assert.False(vm.HasLeftoverItems);
        Assert.Empty(vm.LeftoverGroups);
        Assert.Equal("Nothing left behind. SysManager found no folder or setting of the app you uninstalled.", vm.LeftoverIntro);
    }

    [Fact]
    public async Task OnlyRefusedMatches_SayNothingToRemove_AndListWhatWasSeen()
    {
        var seen = new LeftoverGroup
        {
            AppName = "Acme Notes",
            NotOffered = [@"Not offered: C:\Users\me\Documents\Acme Notes is in Documents, which SysManager never offers."],
        };
        Finds(seen);
        var vm = Vm();

        await UninstallAsync(vm, App("Acme Notes"));

        Assert.Same(seen, Assert.Single(vm.LeftoverGroups));
        Assert.False(vm.HasLeftoverItems);
        Assert.StartsWith("Nothing to remove. A few things named after the app you uninstalled were found",
            vm.LeftoverIntro, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoAppsFindingTheSameFolder_ListItOnce()
    {
        // Two apps of one publisher uninstalled together both find the publisher's folder.
        Finds(Group("Acme Notes", Item(@"C:\Users\me\AppData\Roaming\Acme", LeftoverConfidence.Guess)),
              Group("Acme Draw", Item(@"C:\Users\me\AppData\Roaming\ACME", LeftoverConfidence.Guess),
                                 Item(@"C:\Program Files\Acme Draw")));
        var vm = Vm();

        await UninstallAsync(vm, App("Acme Notes"), App("Acme Draw"));

        Assert.Equal(
            [@"C:\Users\me\AppData\Roaming\Acme", @"C:\Program Files\Acme Draw"],
            vm.LeftoverGroups.SelectMany(g => g.Items).Select(i => i.Location));
        Assert.StartsWith("2 apps were uninstalled.", vm.LeftoverIntro, StringComparison.Ordinal);
    }

    // ── Removing ──────────────────────────────────────────────────────────

    private async Task<UninstallerViewModel> ShowingAsync(params LeftoverGroup[] groups)
    {
        Finds(groups);
        var vm = Vm();
        await UninstallAsync(vm, [.. groups.Select(g => App(g.AppName))]);
        return vm;
    }

    [Fact]
    public async Task Removing_WhenTheUserSaysNo_RemovesNothing()
    {
        var vm = await ShowingAsync(Group("Acme Notes", Item(@"C:\Program Files\Acme Notes")));

        using (var answer = new DialogAnswer(confirm: false))
        {
            await vm.RemoveLeftoversCommand.ExecuteAsync(null);
            Assert.Equal(1, answer.Calls);
        }

        await _leftovers.DidNotReceiveWithAnyArgs().RemoveAsync(default!, default);
        Assert.Single(Assert.Single(vm.LeftoverGroups).Items);
    }

    [Fact]
    public async Task Removing_TakesOnlyWhatIsTicked_AndNeverWhatNeedsAdministratorRights()
    {
        var certain = Item(@"C:\Program Files\Acme Notes");
        var probably = Item(@"C:\Users\me\AppData\Roaming\Acme Notes", LeftoverConfidence.Probably);
        var needsAdmin = Item(@"C:\ProgramData\Acme Notes", LeftoverConfidence.Probably, needsAdmin: true);
        needsAdmin.IsSelected = true;   // a tick the disabled box could never give, made here on purpose
        var vm = await ShowingAsync(Group("Acme Notes", certain, probably, needsAdmin));
        _leftovers.RemoveAsync(Arg.Any<IReadOnlyList<LeftoverItem>>(), Arg.Any<CancellationToken>())
            .Returns(new LeftoverRemoval([certain], 1024, []));
        using var log = new ActivityLogScope();

        using (new DialogAnswer(confirm: true))
            await vm.RemoveLeftoversCommand.ExecuteAsync(null);

        await _leftovers.Received(1).RemoveAsync(
            Arg.Is<IReadOnlyList<LeftoverItem>>(l => l.Count == 1 && l[0] == certain), Arg.Any<CancellationToken>());
        _leftovers.Received(1).ForgetPending(Arg.Is<IReadOnlyCollection<string>>(f => f.SequenceEqual(new[] { certain.Location })));
        Assert.Equal([probably, needsAdmin], Assert.Single(vm.LeftoverGroups).Items);
        Assert.True(vm.ShowLeftovers);
        Assert.Equal("Removed 1 leftover item (1.0 KB).", vm.StatusMessage);
        var entry = Assert.Single(ActivityLogService.Instance.GetRecent(10));
        Assert.Equal(("Uninstaller", "Removed 1 leftover item of uninstalled apps (1.0 KB); folders went to the Recycle Bin"),
            (entry.Action, entry.Detail));
    }

    [Fact]
    public async Task RemovingTheLastItem_HidesTheCard()
    {
        var certain = Item(@"C:\Program Files\Acme Notes");
        var vm = await ShowingAsync(Group("Acme Notes", certain));
        _leftovers.RemoveAsync(Arg.Any<IReadOnlyList<LeftoverItem>>(), Arg.Any<CancellationToken>())
            .Returns(new LeftoverRemoval([certain], 1024, []));

        using (new DialogAnswer(confirm: true))
            await vm.RemoveLeftoversCommand.ExecuteAsync(null);

        Assert.Empty(vm.LeftoverGroups);
        Assert.False(vm.ShowLeftovers);
    }

    [Fact]
    public async Task WhatCouldNotBeRemoved_StaysOnTheList_AndTheStatusSaysWhy()
    {
        var certain = Item(@"C:\Program Files\Acme Notes");
        var vm = await ShowingAsync(Group("Acme Notes", certain));
        _leftovers.RemoveAsync(Arg.Any<IReadOnlyList<LeftoverItem>>(), Arg.Any<CancellationToken>())
            .Returns(new LeftoverRemoval([], 0, [@"C:\Program Files\Acme Notes: Windows did not send it to the Recycle Bin."]));

        using (new DialogAnswer(confirm: true))
            await vm.RemoveLeftoversCommand.ExecuteAsync(null);

        Assert.Same(certain, Assert.Single(Assert.Single(vm.LeftoverGroups).Items));
        Assert.Equal(@"Nothing was removed. C:\Program Files\Acme Notes: Windows did not send it to the Recycle Bin.", vm.StatusMessage);
    }

    [Fact]
    public async Task TheConfirmation_NamesEachItem_AndWhereItGoes()
    {
        var vm = await ShowingAsync(Group("Acme Notes", Item(@"C:\Program Files\Acme Notes", size: 2048)));

        using var answer = new DialogAnswer(confirm: false);
        await vm.RemoveLeftoversCommand.ExecuteAsync(null);

        var shown = Assert.Single(answer.Messages);
        Assert.StartsWith("Remove Leftovers — Confirm\nSend 1 item to the Recycle Bin?", shown, StringComparison.Ordinal);
        Assert.Contains(@"  • C:\Program Files\Acme Notes — 2.0 KB", shown, StringComparison.Ordinal);
        Assert.Contains("so you can put them back from there", shown, StringComparison.Ordinal);
        Assert.Contains("Windows asks before deleting it for good", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingTicked_CannotBeRemoved()
    {
        var vm = await ShowingAsync(Group("Acme Notes", Item(@"C:\Users\me\AppData\Roaming\Acme Notes", LeftoverConfidence.Probably)));

        Assert.False(vm.RemoveLeftoversCommand.CanExecute(null));
        Assert.Equal("Nothing ticked.", vm.LeftoverSelection);

        Assert.Single(vm.LeftoverGroups).Items[0].IsSelected = true;

        Assert.True(vm.RemoveLeftoversCommand.CanExecute(null));
    }

    // ── As administrator ──────────────────────────────────────────────────

    [Fact]
    public async Task AsAdministrator_WhatAnEarlierSessionCouldNotRemove_IsOffered()
    {
        var waiting = Group("Acme Notes", Item(@"C:\Program Files\Acme Notes"));
        _leftovers.LoadPending().Returns([waiting]);

        var vm = Vm(elevated: true);
        await vm.InitializationComplete;

        Assert.Null(vm.InitializationFault);
        Assert.True(vm.ShowLeftovers);
        Assert.Same(waiting, Assert.Single(vm.LeftoverGroups));
        Assert.Equal("Apps you uninstalled earlier left these behind, and removing them needs administrator rights, "
                     + "which SysManager has now. Nothing is ticked: tick what you want removed.", vm.LeftoverIntro);
    }

    [Fact]
    public async Task AsAdministrator_WithNothingWaiting_TheCardStaysHidden()
    {
        var vm = Vm(elevated: true);
        await vm.InitializationComplete;

        _leftovers.Received(1).LoadPending();
        Assert.False(vm.ShowLeftovers);
    }

    [Fact]
    public async Task WithoutAdministratorRights_TheRecordIsNotEvenRead()
    {
        var vm = Vm(elevated: false);
        await vm.InitializationComplete;

        _leftovers.DidNotReceive().LoadPending();
    }

    [Fact]
    public async Task Dismissing_AsAdministrator_ForgetsWhatTheCardShowed()
    {
        _leftovers.LoadPending().Returns([Group("Acme Notes", Item(@"C:\Program Files\Acme Notes"))]);
        var vm = Vm(elevated: true);
        await vm.InitializationComplete;

        vm.DismissLeftoversCommand.Execute(null);

        _leftovers.Received(1).ForgetPending(Arg.Is<IReadOnlyCollection<string>>(f => f.SequenceEqual(new[] { @"C:\Program Files\Acme Notes" })));
        Assert.False(vm.ShowLeftovers);
        Assert.Empty(vm.LeftoverGroups);
    }

    [Fact]
    public async Task Dismissing_WithoutAdministratorRights_KeepsWhatWaitsForThem()
    {
        var vm = await ShowingAsync(Group("Acme Notes", Item(@"C:\Program Files\Acme Notes", needsAdmin: true)));

        vm.DismissLeftoversCommand.Execute(null);

        _leftovers.DidNotReceiveWithAnyArgs().ForgetPending(default!);
        Assert.False(vm.ShowLeftovers);
    }

    // ── Wording ───────────────────────────────────────────────────────────

    [Fact]
    public void TheSelectionLine_CountsAFolderInsideAnotherOnce_AndSaysWhereThingsGo()
    {
        var outer = Item(@"C:\Users\me\AppData\Local\Discord", size: 4096);
        var inner = Item(@"C:\Users\me\AppData\Local\Discord\app-1.0", size: 1024);
        var key = new LeftoverItem { Location = @"Software\Discord\Discord", Kind = LeftoverKind.RegistryKey, Confidence = LeftoverConfidence.OwnKey };

        Assert.Equal("2 ticked · 4.0 KB · goes to the Recycle Bin, so you can put it back",
            UninstallerViewModel.DescribeSelection([outer, inner]));
        Assert.Equal("1 ticked · 0 B · saved to a file first, then deleted", UninstallerViewModel.DescribeSelection([key]));
        Assert.EndsWith("folders go to the Recycle Bin, and registry keys are saved to a file first",
            UninstallerViewModel.DescribeSelection([outer, key]), StringComparison.Ordinal);
    }

    [Fact]
    public void TheConfirmation_ForAKey_SaysWhereItsCopyIsSaved()
    {
        var key = new LeftoverItem { Location = @"Software\Acme\Notes", Kind = LeftoverKind.RegistryKey, Confidence = LeftoverConfidence.OwnKey };

        var text = UninstallerViewModel.DescribeRemoval([key]);

        Assert.StartsWith("Remove 1 leftover item?", text, StringComparison.Ordinal);
        Assert.Contains(@"  • HKEY_CURRENT_USER\Software\Acme\Notes", text, StringComparison.Ordinal);
        Assert.Contains(@"saved as a .reg file first, in %LocalAppData%\SysManager\Backups\Uninstaller", text, StringComparison.Ordinal);
        Assert.Contains("double-click the file to put a key back", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Recycle Bin", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConfirmation_ListsTenItems_AndCountsTheRest()
    {
        var items = Enumerable.Range(1, 12).Select(i => Item($@"C:\Leftover {i:D2}")).ToList();

        var text = UninstallerViewModel.DescribeRemoval(items);

        Assert.Contains(@"C:\Leftover 10", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Leftover 11", text, StringComparison.Ordinal);
        Assert.Contains("… and 2 more", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheActivityLogLine_SaysWhereEachKindWent_AndNamesNothing()
    {
        var folder = Item(@"C:\Program Files\Acme Notes");
        var key = new LeftoverItem { Location = @"Software\Acme\Notes", Kind = LeftoverKind.RegistryKey, Confidence = LeftoverConfidence.OwnKey };

        Assert.Equal("Removed 1 leftover item of uninstalled apps (0 B); registry keys were saved to a file first",
            UninstallerViewModel.DescribeRemovalForTheLog(new LeftoverRemoval([key], 0, [])));
        var both = UninstallerViewModel.DescribeRemovalForTheLog(new LeftoverRemoval([folder, key], 1024, []));
        Assert.Equal("Removed 2 leftover items of uninstalled apps (1.0 KB); folders went to the Recycle Bin, and registry "
                     + "keys were saved to a file first", both);
        Assert.DoesNotContain("Acme", both, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResultLine_SaysWhatWentAndWhatDidNot()
    {
        var item = Item(@"C:\A");

        Assert.Equal("Removed 1 leftover item (1.0 KB). 1 could not be removed. C:\\B: denied.",
            UninstallerViewModel.DescribeRemovalResult(new LeftoverRemoval([item], 1024, [@"C:\B: denied."])));
        Assert.Equal("Nothing was removed.", UninstallerViewModel.DescribeRemovalResult(new LeftoverRemoval([], 0, [])));
    }
}
