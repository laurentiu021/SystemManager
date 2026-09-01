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
}
