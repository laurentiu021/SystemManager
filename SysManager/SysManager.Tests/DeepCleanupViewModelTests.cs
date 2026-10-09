// SysManager · DeepCleanupViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for <see cref="DeepCleanupViewModel"/>.
/// Heavier scan/clean tests that hit the real filesystem live in IntegrationTests.
/// <para>The large-files half moved to <see cref="LargeFilesViewModelTests"/> with the feature (#1523).</para>
/// </summary>
[Collection("ProcessWideStatics")]
public class DeepCleanupViewModelTests : IDisposable
{
    /// <summary>
    /// The scan roots every view model here is built on, redirected at a temp tree this class owns.
    /// </summary>
    /// <remarks>
    /// Not an optimisation. <c>CleanAsync</c> ends with a rescan so the displayed sizes refresh after a
    /// delete — correct product behaviour — and this class used to build its service through the
    /// PARAMETERLESS constructor, which is production's, so that rescan walked the real machine. One test
    /// took <b>170 seconds</b> on a used workstation while the other 31 in this class took 0.12s between
    /// them, and it was 63% of the whole unit suite. It stayed invisible because a scan costs almost
    /// nothing on a fresh CI runner with an empty temp tree, so the local number is the only one that ever
    /// moves (#2333).
    /// <para><c>DeepCleanupService</c> has taken an <c>ICleanupRoots</c> since #2176 and
    /// <see cref="TempCleanupRoots"/> exists for exactly this; <c>DeepCleanupScanLogicTests</c> and
    /// <c>DeepCleanupServiceTests</c> both took it and this class was never brought along.
    /// <c>ArchitectureTests.NoUnitTestBuildsADeepCleanupViewModel_OnTheRealMachinesScanRoots</c> now stops
    /// that happening a third time.</para>
    /// </remarks>
    private readonly TempCleanupRoots _roots = new();

    public void Dispose()
    {
        _roots.Dispose();
        GC.SuppressFinalize(this);
    }

    private DeepCleanupViewModel NewVm() => new(new Services.DeepCleanupService(_roots));

    // ---------- construction & defaults ----------

    [Fact]
    public void Constructor_SetsDefaultScanSummary()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrWhiteSpace(vm.ScanSummary));
        Assert.Contains("Scan", vm.ScanSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_CleanSummaryEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.CleanSummary);
    }

    [Fact]
    public void Constructor_CategoriesEmpty()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Categories);
        Assert.Empty(vm.Categories);
    }

    // ---------- running flags ----------

    [Fact]
    public void IsScanning_DefaultsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public void IsCleaning_DefaultsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsCleaning);
    }

    // ---------- progress defaults ----------

    [Fact]
    public void ScanProgress_DefaultsZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.ScanProgress);
    }

    [Fact]
    public void CleanProgress_DefaultsZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.CleanProgress);
    }

    [Fact]
    public void ScanStatusLine_DefaultsEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.ScanStatusLine);
    }

    [Fact]
    public void CleanStatusLine_DefaultsEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.CleanStatusLine);
    }

    // ---------- computed properties ----------

    [Fact]
    public void TotalSelectedBytes_ZeroWhenNoCategoriesSelected()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.TotalSelectedBytes);
    }

    [Fact]
    public void TotalSelectedDisplay_StartsWithZero()
    {
        var vm = NewVm();
        Assert.StartsWith("0", vm.TotalSelectedDisplay);
    }

    // ---------- TotalSelectedBytes with real categories ----------

    [Fact]
    public void TotalSelectedBytes_SumsSelectedCategories()
    {
        var vm = NewVm();
        var c1 = new CleanupCategory
        {
            Name = "A",
            Description = "a",
            Paths = [],
            TotalSizeBytes = 1000,
            FileCount = 1,
            IsSelected = true
        };
        var c2 = new CleanupCategory
        {
            Name = "B",
            Description = "b",
            Paths = [],
            TotalSizeBytes = 2000,
            FileCount = 2,
            IsSelected = false
        };
        vm.Categories.Add(c1);
        vm.Categories.Add(c2);
        Assert.Equal(1000, vm.TotalSelectedBytes);

        c2.IsSelected = true;
        Assert.Equal(3000, vm.TotalSelectedBytes);
    }

    // ---------- commands exist ----------

    [Theory]
    [InlineData("ScanCommand")]
    [InlineData("CleanCommand")]
    [InlineData("SelectAllCommand")]
    [InlineData("CancelCommand")]
    public void Command_IsExposedAndNotNull(string name)
    {
        var vm = NewVm();
        var prop = vm.GetType().GetProperty(name);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    // ---------- cancel ----------

    [Fact]
    public void CancelCommand_OnIdleVm_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void CancelCommand_RequestsCancellationOnLiveTokenSources()
    {
        var vm = NewVm();
        var scanCts = new CancellationTokenSource();
        var cleanCts = new CancellationTokenSource();

        typeof(DeepCleanupViewModel)
            .GetField("_scanCts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, scanCts);
        typeof(DeepCleanupViewModel)
            .GetField("_cleanCts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, cleanCts);

        vm.CancelCommand.Execute(null);

        Assert.True(scanCts.IsCancellationRequested);
        Assert.True(cleanCts.IsCancellationRequested);
    }

    // ---------- SelectAll ----------

    [Fact]
    public void SelectAll_True_SelectsNonDestructive_SkipsDestructive()
    {
        var vm = NewVm();
        vm.Categories.Add(new CleanupCategory
        {
            Name = "Safe",
            Description = "d",
            Paths = [],
            TotalSizeBytes = 100,
            FileCount = 1,
            IsDestructiveHint = false
        });
        vm.Categories.Add(new CleanupCategory
        {
            Name = "Dangerous",
            Description = "d",
            Paths = [],
            TotalSizeBytes = 200,
            FileCount = 1,
            IsDestructiveHint = true
        });

        vm.SelectAllCommand.Execute(true);

        Assert.True(vm.Categories[0].IsSelected);
        Assert.False(vm.Categories[1].IsSelected);
    }

    [Fact]
    public void SelectAll_False_DeselectsEverything()
    {
        var vm = NewVm();
        var c = new CleanupCategory
        {
            Name = "X",
            Description = "d",
            Paths = [],
            TotalSizeBytes = 100,
            FileCount = 1,
            IsSelected = true
        };
        vm.Categories.Add(c);

        vm.SelectAllCommand.Execute(false);

        Assert.False(c.IsSelected);
    }

    [Fact]
    public void SelectAll_Null_TreatedAsTrue()
    {
        var vm = NewVm();
        vm.Categories.Add(new CleanupCategory
        {
            Name = "X",
            Description = "d",
            Paths = [],
            TotalSizeBytes = 100,
            FileCount = 1,
            IsDestructiveHint = false
        });

        vm.SelectAllCommand.Execute(null);

        Assert.True(vm.Categories[0].IsSelected);
    }

    // ---------- guard conditions ----------

    [Fact]
    public async Task Scan_WhenAlreadyScanning_ReturnsImmediately()
    {
        var vm = NewVm();
        vm.IsScanning = true;
        var before = vm.ScanSummary;

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(before, vm.ScanSummary);
    }

    [Fact]
    public async Task Clean_WhenAlreadyCleaning_ReturnsImmediately()
    {
        var vm = NewVm();
        vm.IsCleaning = true;
        vm.CleanSummary = "marker";

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Equal("marker", vm.CleanSummary);
    }

    [Fact]
    public async Task Clean_WhenNothingSelected_ReturnsImmediately()
    {
        var vm = NewVm();
        vm.CleanSummary = "marker";

        await vm.CleanCommand.ExecuteAsync(null);

        Assert.Equal("marker", vm.CleanSummary);
    }

    // ---------- ShowInExplorer / CopyPath safe calls ----------

    // ---------- setters ----------

    // ---------- ScanLocation record ----------

    // ---------- IsBusy forwarding ----------

    [Fact]
    public void IsScanning_True_SetsIsBusy()
    {
        var vm = NewVm();
        vm.IsScanning = true;
        Assert.True(vm.IsBusy);
    }

    [Fact]
    public void IsScanning_False_ClearsIsBusy()
    {
        var vm = NewVm();
        vm.IsScanning = true;
        vm.IsScanning = false;
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void IsCleaning_True_SetsIsBusy()
    {
        var vm = NewVm();
        vm.IsCleaning = true;
        Assert.True(vm.IsBusy);
    }

    [Fact]
    public void IsBusy_StaysTrueWhileAnyFlagSet()
    {
        var vm = NewVm();
        vm.IsScanning = true;
        vm.IsCleaning = true;
        vm.IsScanning = false;
        Assert.True(vm.IsBusy); // IsCleaning still true
    }

    // ---------- deletion confirmation gate (Round 2a) ----------

    [Fact]
    public async Task Clean_WhenUserDeclinesConfirm_DeletesNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smtest_clean_no_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "keep.dat");
        File.WriteAllText(file, "x");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm();
            vm.Categories.Add(new CleanupCategory
            {
                Name = "Temp",
                Description = "test",
                Paths = new[] { dir },
                TotalSizeBytes = 1,
                FileCount = 1,
                IsSelected = true
            });

            await vm.CleanCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.True(File.Exists(file), "Files were deleted even though the user declined the confirmation");
        }
        finally
        {
            DialogService.Instance = prevDialog;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---------- the Windows Update caches vs a Windows Update install or reset (#2510) ----------

    [Fact]
    public async Task Clean_WithAWindowsUpdateCacheTicked_WhileASystemChangeRuns_RefusesAndDeletesNothing()
    {
        // Reset Windows Update renames the folder both caches live in, and fails while anything inside it is open;
        // an install reads its packages from the download cache. Both hold the system-modification lock.
        var downloads = Path.Combine(Path.GetTempPath(), "smtest_clean_wu_" + Guid.NewGuid().ToString("N"));
        var peerCache = Path.Combine(Path.GetTempPath(), "smtest_clean_do_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(peerCache);
        var downloaded = Path.Combine(downloads, "cab.tmp");
        var shared = Path.Combine(peerCache, "piece.tmp");
        File.WriteAllText(downloaded, "x");
        File.WriteAllText(shared, "x");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Windows Update reset");
        Assert.NotNull(held);
        try
        {
            var vm = NewVm();
            vm.Categories.Add(new CleanupCategory
            {
                Name = "Windows Update cache",
                Description = "test",
                Paths = new[] { downloads },
                TotalSizeBytes = 1,
                FileCount = 1,
                IsSelected = true,
                IsWindowsUpdateCache = true
            });
            vm.Categories.Add(new CleanupCategory
            {
                Name = "Delivery Optimization cache",
                Description = "test",
                Paths = new[] { peerCache },
                TotalSizeBytes = 1,
                FileCount = 1,
                IsSelected = true,
                IsWindowsUpdateCache = true
            });

            await vm.CleanCommand.ExecuteAsync(null);

            Assert.True(File.Exists(downloaded), "the download cache was cleaned while a Windows Update reset ran");
            Assert.True(File.Exists(shared), "the Delivery Optimization cache was cleaned while a Windows Update reset ran");
            Assert.Equal(
                "Cannot start — untick \"Windows Update cache\" and \"Delivery Optimization cache\" or wait: "
                + "Windows Update reset is already running.",
                vm.CleanSummary);
            Assert.False(vm.IsCleaning);
        }
        finally
        {
            DialogService.Instance = prevDialog;
            try { Directory.Delete(downloads, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
            try { Directory.Delete(peerCache, recursive: true); } catch (IOException) { /* ditto */ }
        }
    }

    [Fact]
    public async Task Clean_WithAWindowsUpdateCacheTicked_HoldsTheSystemLockWhileItDeletes()
    {
        // The other direction: an install or a reset started DURING the clean has to be refused too, so the clean
        // holds the lock rather than checking it once at the start. Seen through the lock's own change events:
        // taken while the file is still there, given back once it is gone.
        var dir = Path.Combine(Path.GetTempPath(), "smtest_clean_wu_held_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "cab.tmp");
        File.WriteAllText(file, "x");

        // Enqueued from whichever thread takes or gives back a lock. The handler is named so the process-wide
        // singleton can be unsubscribed below, rather than calling into a test that has finished.
        var seen = new ConcurrentQueue<(string? Holder, bool FileThere)>();
        void onLocksChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e) => seen.Enqueue(
            (OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification), File.Exists(file)));

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        OperationLockService.Instance.PropertyChanged += onLocksChanged;
        try
        {
            var vm = NewVm();
            vm.Categories.Add(new CleanupCategory
            {
                Name = "Windows Update cache",
                Description = "test",
                Paths = new[] { dir },
                TotalSizeBytes = 1,
                FileCount = 1,
                IsSelected = true,
                IsWindowsUpdateCache = true
            });

            await vm.CleanCommand.ExecuteAsync(null);
        }
        finally
        {
            OperationLockService.Instance.PropertyChanged -= onLocksChanged;
            DialogService.Instance = prevDialog;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
        }

        var events = seen.ToArray();
        var taken = Array.FindIndex(events, e => e.Holder == "Deep Cleanup");
        Assert.True(taken >= 0, "the clean never took the system lock");
        Assert.True(events[taken].FileThere, "the clean took the system lock only after it had deleted the file");
        var given = Array.FindIndex(events, taken, e => e.Holder is null);
        Assert.True(given > taken, "the clean never gave the system lock back");
        Assert.False(events[given].FileThere, "the clean gave the system lock back before it deleted the file");
    }

    [Fact]
    public async Task Clean_WithAWindowsUpdateCacheTicked_GivesTheSystemLockBackBeforeTheRescan()
    {
        // The rescan after a clean only reads, and a scan of a used machine was measured at 170 seconds (#2333).
        // Holding the system lock through it would refuse every other system change for that long.
        var dir = Path.Combine(Path.GetTempPath(), "smtest_clean_wu_rescan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cab.tmp"), "x");

        var holders = new ConcurrentQueue<string?>();
        var heldAtRescan = new ConcurrentQueue<bool>();
        void onLocksChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e) =>
            holders.Enqueue(OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification));

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        OperationLockService.Instance.PropertyChanged += onLocksChanged;
        try
        {
            var vm = NewVm();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DeepCleanupViewModel.IsScanning) && vm.IsScanning)
                    heldAtRescan.Enqueue(OperationLockService.Instance.IsLocked(OperationCategory.SystemModification));
            };
            vm.Categories.Add(new CleanupCategory
            {
                Name = "Windows Update cache",
                Description = "test",
                Paths = new[] { dir },
                TotalSizeBytes = 1,
                FileCount = 1,
                IsSelected = true,
                IsWindowsUpdateCache = true
            });

            await vm.CleanCommand.ExecuteAsync(null);
        }
        finally
        {
            OperationLockService.Instance.PropertyChanged -= onLocksChanged;
            DialogService.Instance = prevDialog;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
        }

        Assert.Contains("Deep Cleanup", holders);   // it was taken, or "given back" below would prove nothing
        Assert.False(Assert.Single(heldAtRescan), "the system lock was still held when the read-only rescan began");
    }

    [Fact]
    public async Task Clean_WithOnlyOtherCategoriesTicked_IsNotHeldUpByASystemChange()
    {
        // Scoped to the two categories that conflict: a clean of anything else must not wait for, or hold up, an
        // unrelated system change. It still cleans with the system lock taken by someone else, so it never asked.
        var dir = Path.Combine(Path.GetTempPath(), "smtest_clean_wu_unrelated_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "keep.dat");
        File.WriteAllText(file, "x");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Windows Update install");
        Assert.NotNull(held);
        try
        {
            var vm = NewVm();
            vm.Categories.Add(new CleanupCategory
            {
                Name = "Temp",
                Description = "test",
                Paths = new[] { dir },
                TotalSizeBytes = 1,
                FileCount = 1,
                IsSelected = true
            });

            await vm.CleanCommand.ExecuteAsync(null);

            Assert.False(File.Exists(file), "an unrelated category was held up by a Windows Update install");
        }
        finally
        {
            DialogService.Instance = prevDialog;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
        }
    }

    [Fact]
    public async Task Clean_WhenUserConfirms_DeletesSelectedFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smtest_clean_yes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "delete.dat");
        File.WriteAllText(file, "x");

        // The whole assertion below is "this file is gone", which a file that was never there also satisfies.
        // Stated up front so a broken fixture reads as a broken fixture rather than as a passing delete.
        Assert.True(File.Exists(file), "the fixture file was not created, so the assertion below proves nothing");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // user clicks "Yes"
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm();
            vm.Categories.Add(new CleanupCategory
            {
                Name = "Temp",
                Description = "test",
                Paths = new[] { dir },
                TotalSizeBytes = 1,
                FileCount = 1,
                IsSelected = true
            });

            await vm.CleanCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.False(File.Exists(file), "File survived even though the user confirmed deletion");
        }
        finally
        {
            DialogService.Instance = prevDialog;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── The confirmation must describe what will actually happen ──────────────────────────────────
    //
    // The wording was "These files are removed directly, not sent to the Recycle Bin." Intended as
    // "this is permanent", it reads as "your Recycle Bin is not touched" — while the
    // "Recycle Bin (all drives)" category is pre-ticked whenever the bin is non-empty
    // (DeepCleanupService selects everything with size and no destructive hint). So the one dialog
    // between the user and an emptied Recycle Bin implied the opposite of what Clean would do.
    //
    // Neither test creates a file or cleans anything: the user declines, so the assertion is purely on
    // the message text captured from the dialog.

    private string CapturedCleanPrompt(bool includeRecycleBin)
    {
        string? shown = null;
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Do<string>(m => shown = m), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm();
            vm.Categories.Add(new CleanupCategory
            {
                Name = includeRecycleBin ? "Recycle Bin (all drives)" : "Temp",
                Description = "test",
                Paths = [],
                TotalSizeBytes = 1,
                FileCount = 1,
                IsRecycleBin = includeRecycleBin,
                IsSelected = true
            });

            vm.CleanCommand.Execute(null);
        }
        finally { DialogService.Instance = prevDialog; }

        Assert.NotNull(shown);
        return shown!;
    }

    [Fact]
    public void Clean_WhenTheRecycleBinIsSelected_TheConfirmationSaysSo()
    {
        var prompt = CapturedCleanPrompt(includeRecycleBin: true);

        Assert.Contains("Recycle Bin", prompt, StringComparison.Ordinal);
        Assert.Contains("emptying", prompt, StringComparison.OrdinalIgnoreCase);
        // And it must NOT still carry the old phrasing, which reads as a promise the bin is untouched.
        Assert.DoesNotContain("not sent to the Recycle Bin", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Clean_WhenTheRecycleBinIsNotSelected_StillWarnsThatDeletionIsPermanent()
    {
        // The other half: dropping the misleading sentence must not lose the "this is permanent"
        // warning, which is the reason that sentence existed at all.
        var prompt = CapturedCleanPrompt(includeRecycleBin: false);

        Assert.Contains("cannot be recovered", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("emptying the Recycle Bin", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Escape (#2597) ----------

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Escape_CancelsTheScanOrTheClean_WhicheverRuns(bool scanning, bool cleaning)
    {
        var vm = NewVm();
        Assert.Null(vm.EscapeCancel);

        vm.IsScanning = scanning;
        vm.IsCleaning = cleaning;

        Assert.Same(vm.CancelCommand, vm.EscapeCancel);
    }
}
