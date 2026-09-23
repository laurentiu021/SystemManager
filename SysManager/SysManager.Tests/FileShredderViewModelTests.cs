// SysManager · FileShredderViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="FileShredderViewModel"/>. Verifies initial state,
/// item management, default configuration, and that the irreversible
/// ShredAll command routes through <see cref="DialogService.Instance"/>.Confirm
/// (audit finding tests #2 — the "every destructive op needs Confirm" contract).
/// </summary>
[Collection("ProcessWideStatics")]
public class FileShredderViewModelTests
{
    private static FileShredderViewModel NewVm() =>
        new(new FileShredderService());

    [Fact]
    public void Constructor_ItemsStartsEmpty()
    {
        var vm = NewVm();
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Constructor_SelectedMethodDefaultsToStandard()
    {
        var vm = NewVm();
        Assert.Equal(ShredMethod.Standard, vm.SelectedMethod);
    }

    [Fact]
    public void Constructor_SelectedMethodValueIs3()
    {
        var vm = NewVm();
        Assert.Equal(3, (int)vm.SelectedMethod);
    }

    [Fact]
    public void RemoveItem_RemovesFromList()
    {
        var vm = NewVm();
        var item = new ShredItem
        {
            Path = @"C:\temp\test.txt",
            Name = "test.txt",
            SizeBytes = 1024,
            IsFolder = false
        };
        vm.Items.Add(item);
        Assert.Single(vm.Items);

        vm.RemoveItemCommand.Execute(item);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void RemoveItem_WithNull_DoesNotCrash()
    {
        var vm = NewVm();
        // Should not throw when passing null
        vm.RemoveItemCommand.Execute(null);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void IsShredding_DefaultsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsShredding);
    }

    [Fact]
    public void IsBusy_DefaultsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Items_CanAddMultiple()
    {
        var vm = NewVm();
        vm.Items.Add(new ShredItem { Path = @"C:\a.txt", Name = "a.txt", SizeBytes = 100, IsFolder = false });
        vm.Items.Add(new ShredItem { Path = @"C:\b.txt", Name = "b.txt", SizeBytes = 200, IsFolder = false });
        vm.Items.Add(new ShredItem { Path = @"C:\folder", Name = "folder", SizeBytes = 5000, IsFolder = true });
        Assert.Equal(3, vm.Items.Count);
    }

    // ---------- irreversible-shred confirmation gate (audit tests #2) ----------

    [Fact]
    public async Task ShredAll_WhenUserDeclinesConfirm_ShredsNothing()
    {
        var file = Path.Combine(Path.GetTempPath(), "smtest_shred_no_" + Guid.NewGuid().ToString("N") + ".dat");
        File.WriteAllText(file, "must survive — user declined");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm();
            vm.Items.Add(new ShredItem
            {
                Path = file,
                Name = Path.GetFileName(file),
                SizeBytes = 1,
                IsFolder = false
            });

            await vm.ShredAllCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.True(File.Exists(file), "File was shredded even though the user declined the confirmation");
            Assert.Single(vm.Items); // item left in place — nothing happened
        }
        finally
        {
            DialogService.Instance = prevDialog;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task ShredAll_WhenUserConfirms_ShredsSelectedFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "smtest_shred_yes_" + Guid.NewGuid().ToString("N") + ".dat");
        File.WriteAllText(file, "destroy me — user confirmed");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // user clicks "Yes"
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm();
            vm.Items.Add(new ShredItem
            {
                Path = file,
                Name = Path.GetFileName(file),
                SizeBytes = 1,
                IsFolder = false
            });

            await vm.ShredAllCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.False(File.Exists(file), "File survived even though the user confirmed the shred");
        }
        finally
        {
            DialogService.Instance = prevDialog;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task ShredAll_WithNoItems_NeverPromptsConfirm()
    {
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm(); // Items empty

            await vm.ShredAllCommand.ExecuteAsync(null);

            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ---------- items the user picked but that could not be queued ----------
    // The queue is the user's record of what is about to be destroyed. A file that cannot be read
    // was logged to a file the user never opens and then omitted, so picking ten files and seeing
    // eight looked like the app had simply finished. The message has to name what is missing, and
    // it has to say the item is NOT queued — otherwise "couldn't read it" reads as a warning about
    // something that will still be shredded.

    [Fact]
    public void SkippedMessage_WhenNothingWasSkipped_IsEmptySoNoStaleWarningRemains()
    {
        Assert.Equal(string.Empty, FileShredderViewModel.DescribeSkipped([]));
    }

    [Fact]
    public void SkippedMessage_ForOneItem_NamesItAndSaysItIsNotQueued()
    {
        var message = FileShredderViewModel.DescribeSkipped(["locked.dat"]);

        Assert.Contains("locked.dat", message, StringComparison.Ordinal);
        Assert.Contains("NOT", message, StringComparison.Ordinal);
    }

    [Fact]
    public void SkippedMessage_ForSeveralItems_ReportsTheCountAndEveryName()
    {
        var message = FileShredderViewModel.DescribeSkipped(["a.dat", "b.dat", "c.dat"]);

        Assert.Contains("3", message, StringComparison.Ordinal);
        Assert.Contains("a.dat", message, StringComparison.Ordinal);
        Assert.Contains("b.dat", message, StringComparison.Ordinal);
        Assert.Contains("c.dat", message, StringComparison.Ordinal);
        Assert.Contains("NOT", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The message is only useful if it reaches the screen, and the defect was precisely that a
    /// failure never did. Both add paths must therefore assign StatusMessage on the failure branch —
    /// asserted against the source because the failure needs an unreadable file picked through a
    /// file dialog, which a unit test cannot drive.
    /// </summary>
    [Fact]
    public void BothAddPaths_ReportSkippedItemsOnScreen_NotOnlyToTheLog()
    {
        var source = File.ReadAllText(ViewModelSourcePath());

        var offenders = new List<string>();
        foreach (var command in new[] { "private void AddFiles()", "private void AddFolder()" })
        {
            var start = source.IndexOf(command, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{command} not found — update this guard.");
            var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];

            // Every catch that logs a skipped item must also surface it.
            var logs = body.Split("Log.Warning", StringSplitOptions.None).Length - 1;
            var surfaced = body.Split("StatusMessage", StringSplitOptions.None).Length - 1;
            if (logs == 0)
                offenders.Add($"{command}: no failure logging found — guard is inspecting nothing");
            else if (surfaced == 0)
                offenders.Add($"{command}: {logs} logged failure(s), none reported on screen");
        }

        Assert.True(offenders.Count == 0,
            "an item the user picked can vanish from the shred queue with nothing said on screen:\n  "
            + string.Join("\n  ", offenders));
    }

    private static string ViewModelSourcePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, "SysManager", "ViewModels", "FileShredderViewModel.cs");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not locate FileShredderViewModel.cs from the test output.");
    }
    // ---------- the reason a shred failed has to reach the screen ----------

    [Fact]
    public async Task ShredAll_WhenAFileCannotBeShredded_TheReasonReachesTheStatusLine()
    {
        // The service words its failures for the user — which files were left in place and why. The view
        // model caught the exception, set Status to "Failed", logged a warning, and dropped the message:
        // it contained no ex.Message anywhere. On screen the user saw the single word "Failed" for an
        // operation whose whole point is knowing whether data is gone or still recoverable on disk.
        //
        // An open handle is the deterministic way to provoke it: the shredder opens with FileShare.None,
        // so any other handle makes that open fail. No privileges, no reparse points, no timing.
        var file = Path.Combine(Path.GetTempPath(), "smtest_shredlock_" + Guid.NewGuid().ToString("N") + ".dat");
        await File.WriteAllTextAsync(file, "locked by the test");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            using var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);

            var vm = NewVm();
            vm.Items.Add(new ShredItem
            {
                Path = file,
                Name = Path.GetFileName(file),
                SizeBytes = 1,
                IsFolder = false
            });

            await vm.ShredAllCommand.ExecuteAsync(null);

            // Asserted on the item name rather than the operating system's wording, which is localised.
            Assert.Contains(Path.GetFileName(file), vm.StatusMessage);
            Assert.NotEqual("Complete — 0 shredded, 1 failed.", vm.StatusMessage);
            Assert.StartsWith("Complete — 0 shredded, 1 failed.", vm.StatusMessage);
        }
        finally
        {
            DialogService.Instance = prevDialog;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task ShredAll_WhenEverythingSucceeds_TheStatusLineStaysShort()
    {
        // The negative half. Appending explanations unconditionally would put noise on every clean run,
        // so a shred with nothing to report must produce exactly the summary and not a character more.
        //
        // Queues a FOLDER as well as a file, deliberately. An earlier version used a file only, which
        // meant ShredFolderAsync — and therefore the notice — was never reached, and a mutation making the
        // notice fire unconditionally left this test green. The folder is what exercises the path that can
        // produce noise.
        var file = Path.Combine(Path.GetTempPath(), "smtest_shredclean_" + Guid.NewGuid().ToString("N") + ".dat");
        await File.WriteAllTextAsync(file, "shred me");
        var dir = Path.Combine(Path.GetTempPath(), "smtest_shredcleandir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "inner.dat"), "shred me too");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm();
            vm.Items.Add(new ShredItem
            {
                Path = file,
                Name = Path.GetFileName(file),
                SizeBytes = 1,
                IsFolder = false
            });
            vm.Items.Add(new ShredItem
            {
                Path = dir,
                Name = Path.GetFileName(dir),
                SizeBytes = 1,
                IsFolder = true
            });

            await vm.ShredAllCommand.ExecuteAsync(null);

            Assert.Equal("Complete — 2 shredded, 0 failed.", vm.StatusMessage);
        }
        finally
        {
            DialogService.Instance = prevDialog;
            if (File.Exists(file)) File.Delete(file);
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    // ---------- cancelling the queue still has to report what it destroyed (#2374) ----------

    [Fact]
    public async Task ShredAll_CancelledBetweenItems_ReportsTheItemsItAlreadyDestroyed()
    {
        // Cancelling used to throw past every line that reports: the status line read "Shredding
        // cancelled." with no counts, the accumulated notices were dropped on the floor (the same defect
        // that had already been fixed for the failure path), and the activity log never recorded the items
        // that were in fact destroyed. A cancelled shred is a shred that stopped, not one that did nothing.
        //
        // Deterministic without any timing: ShredItem.Status raises PropertyChanged synchronously inside
        // the queue loop, so cancelling the moment the first item reports "Done" lands exactly on the
        // boundary before the second item is picked up.
        var first = Path.Combine(Path.GetTempPath(), "smtest_shredstop1_" + Guid.NewGuid().ToString("N") + ".dat");
        var second = Path.Combine(Path.GetTempPath(), "smtest_shredstop2_" + Guid.NewGuid().ToString("N") + ".dat");
        await File.WriteAllTextAsync(first, "this one goes");
        const string survivor = "this one must survive the cancel";
        await File.WriteAllTextAsync(second, survivor);

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm();
            var firstItem = new ShredItem
            {
                Path = first,
                Name = Path.GetFileName(first),
                SizeBytes = 1,
                IsFolder = false
            };
            vm.Items.Add(firstItem);
            vm.Items.Add(new ShredItem
            {
                Path = second,
                Name = Path.GetFileName(second),
                SizeBytes = 1,
                IsFolder = false
            });

            firstItem.PropertyChanged += (_, _) =>
            {
                if (firstItem.Status == "Done") vm.CancelCommand.Execute(null);
            };

            await vm.ShredAllCommand.ExecuteAsync(null);

            Assert.Equal("Stopped — 1 shredded, 0 failed.", vm.StatusMessage);
            Assert.False(File.Exists(first), "The item that completed before the cancel was not destroyed");

            // The second file is the whole point: cancelling must stop the queue, not half-destroy the
            // next item. Read it back rather than testing existence — the defect being pinned left a file
            // present with every byte overwritten.
            Assert.Equal(survivor, await File.ReadAllTextAsync(second));

            // The untouched item stays queued; what was destroyed is removed. Counted rather than
            // Assert.Single, which prints the collection — and a ShredItem prints its Path, putting the
            // temp path of whoever ran it into a public CI log.
            Assert.True(vm.Items.Count == 1,
                $"{vm.Items.Count} row(s) left in the queue, expected the one that was never reached.");
        }
        finally
        {
            DialogService.Instance = prevDialog;
            if (File.Exists(first)) File.Delete(first);
            if (File.Exists(second)) File.Delete(second);
        }
    }

    // ---------- a late progress report must not un-finish a shred ----------

    [Fact]
    public async Task ShredAll_RemovesWhatItDestroyed_EvenWhenTheStatusLabelIsOverwrittenAfterwards()
    {
        // The queue used to be rebuilt by re-reading item.Status and removing whatever said "Done". Status
        // is a display string that the per-item Progress<int> callback also writes, and Progress<T>
        // delivers through SynchronizationContext.Post — with no context in a unit test, the post and the
        // await continuation are both unordered ThreadPool work. So the service's last report could drain
        // AFTER the continuation had recorded the outcome, overwriting "Done" with a pass counter: the file
        // was gone and its row stayed in the list claiming it was still being shredded. That is what turned
        // the sibling test above red on CI, once, having passed eight times.
        //
        // Deterministic with no timing at all: the test performs the overwrite itself, from the
        // PropertyChanged that "Done" raises synchronously inside the queue loop. Whatever wrote it, a row
        // whose label is no longer "Done" must still be removed — the file it named does not exist any more.
        var file = Path.Combine(Path.GetTempPath(), "smtest_shredlabel_" + Guid.NewGuid().ToString("N") + ".dat");
        await File.WriteAllTextAsync(file, "destroyed, and the row must go with it");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm();
            var item = new ShredItem
            {
                Path = file,
                Name = Path.GetFileName(file),
                SizeBytes = 1,
                IsFolder = false
            };
            vm.Items.Add(item);

            // Exactly what a late report did. Re-entrant and self-terminating: the nested set assigns a
            // value that is no longer "Done", so the handler runs once more and falls straight through.
            item.PropertyChanged += (_, _) =>
            {
                if (item.Status == "Done") item.Status = "Shredding pass 2/3...";
            };

            await vm.ShredAllCommand.ExecuteAsync(null);

            // Ordered deliberately: if the shred did not happen, the removal proves nothing.
            Assert.False(File.Exists(file), "the file was not shredded, so this test exercised no removal");
            Assert.NotEqual("Done", item.Status); // the overwrite stuck — otherwise the premise is gone

            // Counted, not dumped: Assert.Empty prints the collection, and a ShredItem prints its Path —
            // a temp path carrying the account name of whoever ran it, straight into a public CI log.
            Assert.True(vm.Items.Count == 0,
                $"the file was destroyed and {vm.Items.Count} row(s) stayed in the queue — removal still "
                + "depends on the status label, so an erased file is left on screen as though it were "
                + "still being shredded.");
            Assert.Equal("Complete — 1 shredded, 0 failed.", vm.StatusMessage);
        }
        finally
        {
            DialogService.Instance = prevDialog;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    /// <summary>
    /// The other half of the same defect. The removal no longer depends on the label, but the label is
    /// still what the user reads: a late report overwriting "Cancelled" or "Failed" leaves a finished row
    /// saying "Shredding pass 2/3..." that never changes again. The queue loop therefore reports through
    /// <see cref="SettlingProgress{T}"/> and hands the shred to it, so the reporter has stopped by the time
    /// any final status is written.
    /// </summary>
    /// <remarks>
    /// Asserted against the source rather than by behaviour, deliberately. Whether a report raised after the
    /// operation ends is dropped is behaviour, and <c>SettlingProgressTests</c> measures it there. What no
    /// test of this view model can reach is whether THIS site still goes through that primitive: reproducing
    /// the late delivery needs <c>Progress&lt;T&gt;</c> to post after the await continuation, and the only
    /// lever a test has over those two is installing a <c>SynchronizationContext</c> — which replaces the
    /// scheduling under test with a FIFO queue in which the report always arrives first and the defect cannot
    /// occur. Same reason <c>BothAddPaths_ReportSkippedItemsOnScreen_NotOnlyToTheLog</c> above reads source.
    /// </remarks>
    [Fact]
    public void EveryFinalShredStatus_IsSetThroughTheGateALateReportCannotCross()
    {
        var body = ShredAllBody();

        // Comment tails stripped: the method explains this very fix in prose that quotes "Done" and
        // "Cancelled", and prose is not code. No string literal in this body contains a "//".
        var lines = body.Split('\n')
            .Select(line => line.Split("//", StringSplitOptions.None)[0])
            .ToArray();

        // The reporter settles in SettleAfterAsync's finally, so the handover line is the boundary: a final
        // status written before it is a status the reporter is still free to overwrite.
        var handover = Array.FindIndex(lines,
            line => line.Contains("SettleAfterAsync(", StringComparison.Ordinal));
        Assert.True(handover >= 0,
            "the shred queue no longer hands its operation to SettleAfterAsync, so nothing here stops a "
            + "progress report from overwriting a final status. This guard checks nothing as written.");

        string[] finalStatuses = ["\"Done\"", "\"Cancelled\"", "\"Failed\"", "\"Partly shredded\""];
        var seen = finalStatuses.ToDictionary(status => status, _ => 0, StringComparer.Ordinal);
        var offenders = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var status in finalStatuses)
            {
                if (!lines[i].Contains(status, StringComparison.Ordinal)) continue;

                seen[status]++;
                if (i < handover)
                    offenders.Add($"{status} → {lines[i].Trim()}");
            }
        }

        // Per status, not a total: two of them share one line, so a combined floor would be met by "Done"
        // alone while a renamed "Partly shredded" went unpoliced and this test still read green.
        foreach (var (status, hits) in seen)
            Assert.True(hits >= 1,
                $"the shred queue no longer mentions {status} anywhere, so this guard checks nothing for "
                + "it. Either the status was renamed — update the list — or its branch is gone.");

        Assert.True(offenders.Count == 0,
            "these final statuses are written before the shred is handed to SettleAfterAsync, so the "
            + "reporter is still live when they land: a progress report that drains afterwards overwrites "
            + "them, and the row is left saying it is still being shredded on an item that has finished:\n  "
            + string.Join("\n  ", offenders));

        // And the reporter itself — the statuses above are ordinary assignments now, so they are only safe
        // for as long as the thing reporting over them is the settling one.
        var built = lines.Count(line =>
            line.Contains("new SettlingProgress<int>(", StringComparison.Ordinal));
        Assert.True(built == 1,
            $"expected exactly one SettlingProgress reporter in the queue loop, found {built} — a raw "
            + "Progress<int> here reports straight over whatever the loop wrote last.");
        // Assembled by concatenation, for the reason that guard gives about its own control string: a
        // literal here would be found by the scan that bans a raw Progress<T> in a test — in this very
        // file — and reported as the violation this line exists to prevent.
        Assert.False(string.Join("\n", lines).Contains("new " + "Progress<", StringComparison.Ordinal),
            "a raw Progress<int> is built in the queue loop beside the settling one, and it reports straight "
            + "over whatever the loop wrote last.");

        // One reporter per item, built inside the loop. Hoisting it above the foreach would settle it on the
        // first item and leave every later one with no progress at all.
        var loop = Array.FindIndex(lines,
            line => line.Contains("foreach (var item in Items", StringComparison.Ordinal));
        Assert.True(loop >= 0, "the queue loop over Items is gone — update this guard.");
        Assert.True(Array.FindIndex(lines,
                line => line.Contains("new SettlingProgress<int>(", StringComparison.Ordinal)) > loop,
            "the reporter is built outside the queue loop, so it settles on the first item and every "
            + "later one shreds with no progress reported at all.");

        // Every shred goes through the wrapper. An awaited service call would run with a reporter nothing
        // settles, which is the original bug with a shared primitive sitting unused next to it.
        var serviceCalls = lines.Count(line => line.Contains("_service.Shred", StringComparison.Ordinal));
        Assert.True(serviceCalls >= 2,
            $"found {serviceCalls} shred calls in the queue loop, expected the file and folder branches — "
            + "the slice or the branch names changed, so the check below proves nothing.");
        var handovers = lines.Count(line => line.Contains("SettleAfterAsync(", StringComparison.Ordinal));
        Assert.True(serviceCalls == handovers,
            $"{serviceCalls} shred calls in the queue loop against {handovers} handed to SettleAfterAsync. A "
            + "shred awaited directly runs with a reporter nothing settles, so its last report overwrites "
            + "whichever final status its branch writes — half a migration is the original defect with the "
            + "primitive sitting unused beside it.");
        Assert.False(string.Join("\n", lines).Contains("await _service.", StringComparison.Ordinal),
            "a shred is awaited straight from the service instead of through the reporter's SettleAfterAsync; "
            + "the count above says how many.");
    }

    /// <summary>
    /// The body of the shred queue loop, with a floor so a wrong slice cannot read as health.
    /// </summary>
    private static string ShredAllBody()
    {
        var source = File.ReadAllText(ViewModelSourcePath());
        const string signature = "private async Task ShredAllAsync()";

        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found — update this guard.");

        // The method's own closing brace: four spaces, since every brace inside it is indented deeper.
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of " + signature);

        var body = source[start..end];
        var length = body.Split('\n').Length;
        Assert.True(length >= 100,
            $"only {length} lines of {signature} were extracted, against the ~170 it spans — the slice is "
            + "wrong, so a pass here proves nothing.");

        return body;
    }
}
