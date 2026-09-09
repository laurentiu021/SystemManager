// SysManager · StartupViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="StartupViewModel"/>. Verifies initial state,
/// commands, and scan summary logic.
/// Serialized under the DialogService collection: the confirm-gate test swaps the
/// process-wide static <see cref="DialogService.Instance"/>.
/// </summary>
[Collection("ProcessWideStatics")]
public class StartupViewModelTests
{
    /// <summary>
    /// A view-model with the elevation probe pinned to <paramref name="elevated"/>.
    /// </summary>
    /// <remarks>
    /// Unelevated by default, and that is the point rather than a convenience: the scan reads Windows'
    /// boot-delay events only when elevated, so a suite run from an administrator console would have every
    /// one of these tests open an event log and depend on what that machine last measured. The probe is
    /// injected so the test decides, not the console it was started from.
    /// </remarks>
    private static StartupViewModel NewVm(bool elevated = false,
                                          Func<Task<IReadOnlyList<BootDegradation>>>? readDegradations = null)
        => new(new StartupService(), () => elevated,
               readDegradations ?? (() => Task.FromResult<IReadOnlyList<BootDegradation>>([])));

    [Fact]
    public void Constructor_EntriesCollectionNotNull()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Entries);
    }

    [Fact]
    public void Constructor_CommandsExist()
    {
        var vm = NewVm();
        Assert.NotNull(vm.ScanCommand);
        Assert.NotNull(vm.ToggleEntryCommand);
        Assert.NotNull(vm.EnableAllCommand);
        Assert.NotNull(vm.OpenFileLocationCommand);
    }

    [Fact]
    public void Constructor_DefaultCounts()
    {
        var vm = NewVm();
        // Before scan completes, counts should be 0
        Assert.Equal(0, vm.EnabledCount);
        Assert.Equal(0, vm.DisabledCount);
        Assert.Equal(0, vm.TotalCount);
    }

    [Fact]
    public void ScanSummary_HasDefaultValue()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrEmpty(vm.ScanSummary));
    }

    [Fact]
    public async Task ScanAsync_PopulatesEntries()
    {
        var vm = NewVm();
        // The constructor fires the scan and forgets it; this is the task it started.
        await vm.InitializationComplete;
        // On any Windows machine there should be at least 1 startup item.
        Assert.True(vm.Entries.Count > 0, "Expected at least one startup entry");
        Assert.True(vm.TotalCount > 0);
    }

    [Fact]
    public async Task ScanAsync_UpdatesScanSummary()
    {
        var vm = NewVm();
        // The constructor fires the scan and forgets it; this is the task it started.
        await vm.InitializationComplete;
        // After scan, summary should contain counts if entries were found
        if (vm.TotalCount > 0)
            Assert.Contains("enabled", vm.ScanSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanAsync_CountsAreConsistent()
    {
        var vm = NewVm();
        // The constructor fires the scan and forgets it; this is the task it started.
        await vm.InitializationComplete;
        Assert.Equal(vm.Entries.Count, vm.TotalCount);
        Assert.Equal(vm.EnabledCount + vm.DisabledCount, vm.TotalCount);
    }

    [Fact]
    public void ToggleEntry_NullDoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.ToggleEntryCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void ToggleEntry_WrongTypeDoesNotThrow()
    {
        var vm = NewVm();
        // Simulates WPF DataGrid virtualization passing a non-StartupEntry object
        var ex = Record.Exception(() => vm.ToggleEntryCommand.Execute("not a StartupEntry"));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenFileLocation_NullDoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.OpenFileLocationCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenFileLocation_WrongTypeDoesNotThrow()
    {
        var vm = NewVm();
        // Simulates WPF DataGrid virtualization passing a non-StartupEntry object
        var ex = Record.Exception(() => vm.OpenFileLocationCommand.Execute(42));
        Assert.Null(ex);
    }

    // ── re-entrancy guard (regression: overlapping registry writes) ──

    // ── confirmation gate (regression: bulk Enable All is a system change, must confirm) ──

    [Fact]
    public async Task EnableAll_WhenUserDeclinesConfirm_DoesNotEnableAndLeavesEntriesDisabled()
    {
        // Regression: "Enable All" re-arms every disabled startup item (registry/task writes)
        // and adds boot time, so it must ask first. Declining must short-circuit BEFORE any
        // write — proven here by the entry staying disabled (SetEnabledAsync is never reached).
        var vm = NewVm();
        // Wait for the ctor's auto-scan to finish before seeding, so it cannot overwrite the seeded
        // entry mid-test. The task itself, not a sampled IsBusy flag.
        await vm.InitializationComplete;

        vm.Entries.Clear();
        var disabled = new StartupEntry { Name = "Seeded", Command = "c:\\x.exe", IsEnabled = false };
        vm.Entries.Add(disabled);

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            await vm.EnableAllCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.False(disabled.IsEnabled); // never enabled — the write path was not taken
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public async Task EnableAll_WithNoDisabledEntries_DoesNotPrompt()
    {
        // Nothing to enable → no confirmation dialog (and no write). Guards against nagging.
        var vm = NewVm();
        // Wait for the ctor's auto-scan to finish before seeding, so it cannot overwrite the seeded
        // entry mid-test. The task itself, not a sampled IsBusy flag.
        await vm.InitializationComplete;

        vm.Entries.Clear();
        vm.Entries.Add(new StartupEntry { Name = "On", Command = "c:\\y.exe", IsEnabled = true });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        DialogService.Instance = dialog;
        try
        {
            await vm.EnableAllCommand.ExecuteAsync(null);
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void StateChangingCommands_DisabledWhileBusy()
    {
        // Scan, EnableAll and ToggleEntry all read or write the same startup registry/task
        // state; the NotBusy gate stops them overlapping and interleaving registry writes.
        // Drive IsBusy explicitly rather than asserting the post-construction baseline: the
        // constructor kicks off an async auto-scan that briefly sets IsBusy itself.
        var vm = NewVm();

        vm.IsBusy = true;
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.EnableAllCommand.CanExecute(null));
        Assert.False(vm.ToggleEntryCommand.CanExecute(null));

        vm.IsBusy = false;
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.EnableAllCommand.CanExecute(null));
        Assert.True(vm.ToggleEntryCommand.CanExecute(null));
    }
    // ---------- boot impact attribution (#1587) ----------

    private static BootDegradation Slow(string name, long ms, int day = 1, string kind = "Application")
        => new(new DateTime(2026, 9, day, 8, 0, 0, DateTimeKind.Local), kind, name, ms);

    [Theory]
    [InlineData("Discord")]           // Windows reported the friendly name; matches the entry name
    [InlineData("discord")]           // and case never decides an attribution
    [InlineData("DISCORD")]
    public void MatchDegradation_MatchesTheEntryNameWholeAndCaseInsensitively(string reported)
    {
        var entry = new StartupEntry { Name = "Discord", Command = @"C:\Users\x\Discord\app.exe --start" };
        var match = StartupViewModel.MatchDegradation(entry, [Slow(reported, 3200)]);

        Assert.NotNull(match);
        Assert.Equal(3200, match!.DurationMs);
    }

    [Theory]
    [InlineData("app.exe")]                          // reported as the bare file name
    [InlineData(@"C:\Users\x\Discord\app.exe")]  // or as a full path, which reduces to the same name
    public void MatchDegradation_MatchesTheExecutableFileName(string reported)
    {
        // The entry's own name is nothing like the executable, which is the ordinary case for a Run key
        // whose value name was chosen by an installer.
        var entry = new StartupEntry { Name = "SomeInstallerName", Command = @"C:\Users\x\Discord\app.exe --start" };
        Assert.NotNull(StartupViewModel.MatchDegradation(entry, [Slow(reported, 1500)]));
    }

    [Theory]
    [InlineData("Disc")]              // a prefix of the name
    [InlineData("Discord Client")]    // the name as a prefix of the report
    [InlineData("MyDiscord")]         // a substring match
    [InlineData("other.exe")]         // a different executable
    [InlineData("")]                  // nothing reported at all
    public void MatchDegradation_RefusesAnythingShortOfAWholeMatch(string reported)
    {
        // Fail closed. A near miss here tells someone a program they depend on cost them three seconds, on a
        // tab whose one action is to switch that program off.
        var entry = new StartupEntry { Name = "Discord", Command = @"C:\Users\x\Discord\app.exe" };
        Assert.Null(StartupViewModel.MatchDegradation(entry, [Slow(reported, 3200)]));
    }

    [Fact]
    public void MatchDegradation_WithNoNameAndNoExecutable_MatchesNothing()
    {
        // Both sides empty must not read as equal, or an unnamed entry would inherit every unnamed report.
        var entry = new StartupEntry { Name = "", Command = "" };
        Assert.Null(StartupViewModel.MatchDegradation(entry, [Slow("", 900), Slow("chrome.exe", 900)]));
    }

    [Fact]
    public void MatchDegradation_TakesTheNewestMeasurement()
    {
        // The column says "the last start-up", so an entry that was slow once and is not any more must stop
        // saying so rather than keeping its worst number.
        var entry = new StartupEntry { Name = "Discord", Command = @"C:\x\app.exe" };
        var match = StartupViewModel.MatchDegradation(entry,
            [Slow("Discord", 9000, day: 1), Slow("Discord", 400, day: 5), Slow("Discord", 7000, day: 3)]);

        Assert.NotNull(match);
        Assert.Equal(400, match!.DurationMs);
    }

    [Fact]
    public void ApplyBootImpact_FillsInTheFigureTheDetailAndTheSortKey()
    {
        var matched = new StartupEntry { Name = "Discord", Command = @"C:\x\app.exe" };
        var unmatched = new StartupEntry { Name = "Steam", Command = @"C:\x\steam.exe" };

        StartupViewModel.ApplyBootImpact([matched, unmatched], [Slow("Discord", 3200)]);

        Assert.Equal("3.2 s", matched.StartupImpact);
        Assert.Equal(3200, matched.StartupImpactMs);
        Assert.Contains("3.2 s", matched.StartupImpactDetail, StringComparison.Ordinal);
        Assert.Contains("2026-09-01", matched.StartupImpactDetail, StringComparison.Ordinal);

        // Not "0 s", not "None". A zero would be a claim that Steam is fast, and nothing measured it.
        Assert.Equal("", unmatched.StartupImpact);
        Assert.Equal(0, unmatched.StartupImpactMs);
        Assert.Equal("", unmatched.StartupImpactDetail);
    }

    [Fact]
    public void ApplyBootImpact_WithNothingMeasured_LeavesEveryEntryBlank()
    {
        var entry = new StartupEntry { Name = "Discord", Command = @"C:\x\app.exe" };
        StartupViewModel.ApplyBootImpact([entry], []);
        Assert.Equal("", entry.StartupImpact);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Foo\bar.exe"" --flag", "bar.exe")]
    [InlineData(@"C:\Program Files\Foo\bar.exe --flag", "bar.exe")]
    [InlineData("bar.exe", "bar.exe")]
    // The first extension in the string wins, so an argument naming another file cannot take over.
    [InlineData(@"C:\Windows\rundll32.exe C:\x\thing.dll,Entry", "rundll32.exe")]
    [InlineData(@"C:\x\slowdriver.sys", "slowdriver.sys")]
    [InlineData("Some Friendly App Name", "")]
    [InlineData("", "")]
    public void ExecutableFileName_ReadsTheFileNameWithoutTouchingTheDisk(string command, string expected)
        => Assert.Equal(expected, StartupViewModel.ExecutableFileName(command));

    [Fact]
    public void ExecutableFileName_DoesNotDependOnThePathExisting()
    {
        // The reason this is not ExtractExecutablePath, which probes File.Exists to decide where an unquoted
        // path ends: attribution has to give the same answer on a machine where the program is not installed.
        const string absent = @"Q:\nowhere\definitely-not-here\ghost.exe --flag";
        Assert.False(System.IO.File.Exists(absent));
        Assert.Equal("ghost.exe", StartupViewModel.ExecutableFileName(absent));
    }

    [Fact]
    public async Task Scan_WhenNotElevated_NeverAsksWindowsForBootMeasurements()
    {
        // Asserted on whether the reader is CALLED, not on the outcome. The outcome cannot prove this gate:
        // the real reader also returns nothing when the rights are missing, so with the gate deleted an
        // unelevated run still shows an empty column while paying to open an event log on every scan.
        var reads = 0;
        var vm = NewVm(elevated: false, readDegradations: () =>
        {
            reads++;
            return Task.FromResult<IReadOnlyList<BootDegradation>>([Slow("Discord", 3200)]);
        });
        await vm.InitializationComplete;

        Assert.Equal(0, reads);
        Assert.All(vm.Entries, e => Assert.Equal("", e.StartupImpact));
    }

    [Fact]
    public async Task Scan_WhenElevated_AsksWindowsForBootMeasurements()
    {
        // The other side of the same branch, so the gate cannot be satisfied by never reading at all.
        var reads = 0;
        var vm = NewVm(elevated: true, readDegradations: () =>
        {
            reads++;
            return Task.FromResult<IReadOnlyList<BootDegradation>>([]);
        });
        await vm.InitializationComplete;

        Assert.Equal(1, reads);
    }

}
