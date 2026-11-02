// SysManager · StartupToggleTests — tests for SetEnabled toggle logic
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Text;
using Microsoft.Win32;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="StartupService.SetEnabledAsync(StartupEntry, bool)"/> covering registry-based
/// and Task Scheduler entries, plus specific error messages (#176), and for the scan's read of the same
/// StartupApproved lists (#2741).
/// </summary>
public sealed class StartupToggleTests : IDisposable
{
    // Throwaway keys standing in for HKCU and HKLM. A toggle writes StartupApproved under the root it is given, and on
    // the real hive every run of the suite left one more value in the user's own list (#2740).
    private readonly string _rootName = @"Software\SysManagerTests\StartupToggle_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _currentUser;
    private readonly RegistryKey _localMachine;

    // schtasks runs through the runner, so a scheduled task is switched against a substitute rather than a real task.
    private readonly IPowerShellRunner _runner = Substitute.For<IPowerShellRunner>();
    private readonly HeldTimers _clock = new();
    private readonly StartupService _service;

    public StartupToggleTests()
    {
        _currentUser = Registry.CurrentUser.CreateSubKey(_rootName + @"\HKCU", writable: true);
        _localMachine = Registry.CurrentUser.CreateSubKey(_rootName + @"\HKLM", writable: true);
        _service = new StartupService(_runner, _clock);
    }

    public void Dispose()
    {
        _service.Dispose();
        _currentUser.Dispose();
        _localMachine.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);
    }

    private Task<bool> SetEnabledAsync(StartupEntry entry, bool enabled)
        => _service.SetEnabledAsync(entry, enabled, _currentUser, _localMachine);

    /// <summary>What schtasks answers through the runner: the lines it prints on its error stream, and its exit code.</summary>
    private void Schtasks(int exitCode, params string[] errors)
        => _runner.RunProcessAsync("schtasks.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Encoding?>())
            .Returns(_ =>
            {
                foreach (var line in errors)
                    _runner.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Err(line));
                return Task.FromResult(exitCode);
            });

    /// <summary>A scheduled-task row under a folder no real task uses.</summary>
    private static StartupEntry ATask(string name = "Updater") => new()
    {
        Name = name,
        Command = "updater.exe",
        Source = StartupSource.TaskScheduler,
        TaskPath = @"\SysManagerTests\" + name,
        IsEnabled = true,
        StatusText = "Enabled",
    };

    [Fact]
    public async Task SetEnabledAsync_TaskScheduler_EmptyPath_ReturnsFalseWithMessage()
    {
        var entry = new StartupEntry
        {
            Name = "TestTask",
            Command = "test.exe",
            Source = StartupSource.TaskScheduler,
            TaskPath = "",
            IsEnabled = true,
            StatusText = "Enabled"
        };

        var result = await SetEnabledAsync(entry, false);

        Assert.False(result);
        Assert.Contains("task path unknown", entry.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetEnabledAsync_TaskScheduler_NullPath_ReturnsFalseWithMessage()
    {
        var entry = new StartupEntry
        {
            Name = "TestTask",
            Command = "test.exe",
            Source = StartupSource.TaskScheduler,
            TaskPath = null!,
            IsEnabled = true,
            StatusText = "Enabled"
        };

        var result = await SetEnabledAsync(entry, false);

        Assert.False(result);
        Assert.Contains("task path unknown", entry.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetEnabledAsync_RegistryEntry_NeverShowsGenericAdminMessage()
    {
        var entry = new StartupEntry
        {
            Name = "FakeEntry",
            Command = "fake.exe",
            Source = StartupSource.RegistryCurrentUser,
            RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            ValueName = "NonExistentTestEntry_" + Guid.NewGuid().ToString("N"),
            IsEnabled = true,
            StatusText = "Enabled"
        };

        var result = await SetEnabledAsync(entry, false);

        Assert.DoesNotContain("may need admin", entry.StatusText, StringComparison.OrdinalIgnoreCase);

        // The disable blob went to the throwaway root, and nothing reached the user's own list.
        Assert.True(result);
        using (var approved = _currentUser.OpenSubKey(StartupService.ApprovedRunHKCU))
            Assert.Equal((byte)3, Assert.IsType<byte[]>(approved?.GetValue(entry.ValueName))[0]);
        using (var real = Registry.CurrentUser.OpenSubKey(StartupService.ApprovedRunHKCU))
            Assert.Null(real?.GetValue(entry.ValueName));
    }

    [Fact]
    public async Task SetEnabledAsync_TaskScheduler_WithBogusPath_ReturnsFalseWithSpecificError()
    {
        // What schtasks prints for a task that does not exist. It ran the real one with this path.
        Schtasks(exitCode: 1, "ERROR: The system cannot find the file specified.");
        var entry = new StartupEntry
        {
            Name = "BogusTask",
            Command = "bogus.exe",
            Source = StartupSource.TaskScheduler,
            TaskPath = @"\NonExistent\BogusTask_" + Guid.NewGuid().ToString("N"),
            IsEnabled = true,
            StatusText = "Enabled"
        };

        var result = await SetEnabledAsync(entry, false);

        Assert.False(result);
        Assert.StartsWith("Error", entry.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("may need admin", entry.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetEnabledAsync_PreservesEntryState_OnFailure()
    {
        var entry = new StartupEntry
        {
            Name = "TestTask",
            Command = "test.exe",
            Source = StartupSource.TaskScheduler,
            TaskPath = "",
            IsEnabled = true,
            StatusText = "Enabled"
        };

        await SetEnabledAsync(entry, false);

        Assert.True(entry.IsEnabled);
    }

    // ---------- a scheduled task, switched by schtasks through the runner (#2766) ----------

    /// <summary>
    /// Turning a scheduled task off runs schtasks through the runner, read in the OEM code page, and the row follows
    /// what it answered (#2766).
    /// </summary>
    /// <remarks>
    /// The service started schtasks itself, so no test could flip this switch without changing a real task.
    /// </remarks>
    [Fact]
    public async Task ATaskSwitch_RunsSchtasksThroughTheRunner_AndTheRowFollowsIt()
    {
        Schtasks(exitCode: 0);
        var entry = ATask();

        Assert.True(await SetEnabledAsync(entry, false));

        await _runner.Received(1).RunProcessAsync("schtasks.exe", "/Change /TN \"\\SysManagerTests\\Updater\" /Disable",
            Arg.Any<CancellationToken>(), PowerShellRunner.OemEncoding);
        Assert.False(entry.IsEnabled);
        Assert.Equal(StartupService.TaskStatus(false, entry.StartsWithWindows), entry.StatusText);
    }

    /// <summary>Where schtasks refuses, the row says what it printed and keeps its state (#2766).</summary>
    [Fact]
    public async Task ATaskSwitchThatSchtasksRefuses_SaysWhatItPrinted_AndKeepsTheRow()
    {
        Schtasks(exitCode: 1, "ERROR: Access is denied.");
        var entry = ATask();

        Assert.False(await SetEnabledAsync(entry, false));

        Assert.Equal("Error — ERROR: Access is denied.", entry.StatusText);
        Assert.True(entry.IsEnabled);
    }

    /// <summary>
    /// A schtasks run that does not finish is cancelled when the timeout runs out, which is what makes the runner stop
    /// it, and the row says it timed out (#2766).
    /// </summary>
    [Fact]
    public async Task ATaskSwitchThatHangs_IsCancelledAtTheTimeout_AndSaysSo()
    {
        CancellationToken given = default;
        _runner.RunProcessAsync("schtasks.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Encoding?>())
            .Returns(call =>
            {
                given = call.Arg<CancellationToken>();
                var hung = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                given.Register(() => hung.TrySetCanceled(given));
                return hung.Task;
            });
        var entry = ATask();

        var switching = SetEnabledAsync(entry, false);
        Assert.False(switching.IsCompleted);
        Assert.Equal(StartupService.TaskChangeTimeout, _clock.OnlyDue);
        _clock.FireAll();

        Assert.False(await switching.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(given.IsCancellationRequested);
        Assert.Equal("Error — schtasks timed out", entry.StatusText);
        Assert.True(entry.IsEnabled);
    }

    /// <summary>Where schtasks cannot be started at all, the row says it is not available (#2766).</summary>
    [Fact]
    public async Task ATaskSwitchWhoseSchtasksCannotStart_SaysItIsNotAvailable()
    {
        _runner.RunProcessAsync("schtasks.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Encoding?>())
            .Returns(Task.FromException<int>(new Win32Exception(2)));
        var entry = ATask();

        Assert.False(await SetEnabledAsync(entry, false));

        Assert.Equal("Error — schtasks not available", entry.StatusText);
    }

    /// <summary>
    /// Two switches at once run schtasks one after the other: what one run prints reaches the service through the
    /// runner's one event, so two at once could give a row the other's error (#2766).
    /// </summary>
    [Fact]
    public async Task TwoTaskSwitchesAtOnce_RunSchtasksOneAfterTheOther()
    {
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _runner.RunProcessAsync("schtasks.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Encoding?>())
            .Returns(_ => Interlocked.Increment(ref calls) == 1 ? first.Task : Task.FromResult(0));

        var one = SetEnabledAsync(ATask("One"), false);
        var two = SetEnabledAsync(ATask("Two"), false);

        Assert.Equal(1, Volatile.Read(ref calls));
        first.SetResult(0);
        Assert.True(await one.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await two.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    // ---------- RunOnce entries can't be toggled via StartupApproved (#1428) ----------

    [Theory]
    [InlineData(StartupSource.RegistryCurrentUser)]
    [InlineData(StartupSource.RegistryLocalMachine)]
    [InlineData(StartupSource.RegistryLocalMachine32)]
    public async Task SetEnabledAsync_RunOnce_ReturnsFalseWithHonestMessage_AndDoesNotDisable(StartupSource source)
    {
        // Regression (#1428): Windows has no StartupApproved\RunOnce and never consults
        // StartupApproved for RunOnce keys, so the old code wrote the disable blob to
        // StartupApproved\Run and returned true — the item still ran at next boot while the
        // UI showed "Disabled". The toggle must now report a truthful non-success and leave
        // IsEnabled untouched (the RunOnce guard short-circuits before any registry write).
        var entry = new StartupEntry
        {
            Name = "OneShotSetup",
            Command = "setup.exe /oncemore",
            Source = source,
            RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            ValueName = "OneShotSetup",
            IsEnabled = true,
            StatusText = "Enabled"
        };

        var result = await SetEnabledAsync(entry, false);

        Assert.False(result);
        Assert.Contains("run-once", entry.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(entry.IsEnabled); // never flipped to disabled
        Assert.Empty(_currentUser.GetSubKeyNames());
        Assert.Empty(_localMachine.GetSubKeyNames());
    }

    // ---------- each source writes the StartupApproved list Windows reads for it ----------
    // Asserted against the source until SetEnabledAsync took its hive roots, because the only registry it could
    // reach was the live one. A blob in the wrong list is the failure that shipped twice: the item keeps starting at
    // every boot while the tab says "Disabled".

    private const string ApprovedLists = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    [Theory]
    [InlineData(StartupSource.RegistryCurrentUser, "HKCU", "Run")]
    [InlineData(StartupSource.StartupFolder, "HKCU", "StartupFolder")]
    [InlineData(StartupSource.RegistryLocalMachine, "HKLM", "Run")]
    [InlineData(StartupSource.RegistryLocalMachine32, "HKLM", "Run32")]
    [InlineData(StartupSource.CommonStartupFolder, "HKLM", "StartupFolder")]
    public async Task Disabling_WritesTheApprovedKeyWindowsReadsForItsSource(StartupSource source, string hive, string list)
    {
        var entry = new StartupEntry
        {
            Name = "Probe",
            Command = "probe.exe",
            Source = source,
            RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            ValueName = "Probe",
            IsEnabled = true,
            StatusText = "Enabled"
        };

        Assert.True(await SetEnabledAsync(entry, false));

        var (written, untouched) = hive == "HKCU" ? (_currentUser, _localMachine) : (_localMachine, _currentUser);
        foreach (var name in new[] { "Run", "Run32", "StartupFolder" })
        {
            using var key = written.OpenSubKey($@"{ApprovedLists}\{name}");
            if (name == list)
                Assert.Equal((byte)3, Assert.IsType<byte[]>(key?.GetValue("Probe"))[0]);
            else
                Assert.Null(key?.GetValue("Probe"));
        }
        Assert.Empty(untouched.GetSubKeyNames());
        Assert.False(entry.IsEnabled);
    }

    [Fact]
    public async Task APolicyRunItem_IsRefused_AndNothingIsWritten()
    {
        // Windows never consults StartupApproved for a policy key, so a disable blob anywhere would be one nothing reads.
        var entry = new StartupEntry
        {
            Name = "Pushed",
            Command = "pushed.exe",
            Source = StartupSource.PolicyRun,
            RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run",
            ValueName = "Pushed",
            IsEnabled = true,
            StatusText = "Enabled"
        };

        Assert.False(await SetEnabledAsync(entry, false));

        Assert.Contains("system policy", entry.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(entry.IsEnabled);
        Assert.Empty(_currentUser.GetSubKeyNames());
        Assert.Empty(_localMachine.GetSubKeyNames());
    }

    // ---------- each source reads the StartupApproved list Windows reads for it (#2741) ----------
    // The scan's read of these lists could only run over the live registry, and its one test checked that IsEnabled
    // could be read, which a bool always can: an inverted bit or the wrong list would have passed.

    private static readonly string[] Hives = ["HKCU", "HKLM"];
    private static readonly string[] Lists = ["Run", "Run32", "StartupFolder"];

    /// <summary>An item as the scan builds it, before the lists are read: on, and saying so.</summary>
    private static StartupEntry Scanned(StartupSource source, string valueName = "Probe",
        string registryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run") => new()
        {
            Name = valueName,
            Command = "probe.exe",
            Source = source,
            RegistryKey = registryKey,
            ValueName = valueName,
            IsEnabled = true,
            StatusText = "Enabled"
        };

    /// <summary>Writes a 12-byte blob whose first byte is <paramref name="state"/>, as Task Manager does.</summary>
    private void Approve(string hive, string list, string valueName, byte state)
    {
        using var key = (hive == "HKCU" ? _currentUser : _localMachine).CreateSubKey($@"{ApprovedLists}\{list}");
        var blob = new byte[12];
        blob[0] = state;
        key.SetValue(valueName, blob, RegistryValueKind.Binary);
    }

    private StartupEntry ReadBack(StartupEntry entry)
    {
        StartupService.ApplyApprovedState([entry], _currentUser, _localMachine);
        return entry;
    }

    [Theory]
    [InlineData((byte)0x02, true)]
    [InlineData((byte)0x03, false)]
    [InlineData((byte)0x06, true)]   // Windows 11 writes 06 and 07 as well
    [InlineData((byte)0x07, false)]
    public void TheStateIsTheLowBitOfTheBlobsFirstByte(byte state, bool enabled)
    {
        Approve("HKCU", "Run", "Probe", state);

        var entry = ReadBack(Scanned(StartupSource.RegistryCurrentUser));

        Assert.Equal(enabled, entry.IsEnabled);
        Assert.Equal(enabled ? "Enabled" : "Disabled", entry.StatusText);
    }

    [Theory]
    [InlineData(StartupSource.RegistryCurrentUser, "HKCU", "Run")]
    [InlineData(StartupSource.StartupFolder, "HKCU", "StartupFolder")]
    [InlineData(StartupSource.RegistryLocalMachine, "HKLM", "Run")]
    [InlineData(StartupSource.RegistryLocalMachine32, "HKLM", "Run32")]
    [InlineData(StartupSource.CommonStartupFolder, "HKLM", "StartupFolder")]
    public void EachSource_ReadsTheApprovedListWindowsReadsForIt(StartupSource source, string hive, string list)
    {
        // Off in its own list and on in every other, so only a read of the right list says off.
        foreach (var h in Hives)
            foreach (var l in Lists)
                Approve(h, l, "Probe", h == hive && l == list ? (byte)3 : (byte)2);

        Assert.False(ReadBack(Scanned(source)).IsEnabled);
    }

    [Theory]
    [InlineData(StartupSource.PolicyRun)]
    [InlineData(StartupSource.TaskScheduler)]
    public void ASourceWithNoApprovedList_IsReadFromNone(StartupSource source)
    {
        foreach (var h in Hives)
            foreach (var l in Lists)
                Approve(h, l, "Probe", 3);

        Assert.True(ReadBack(Scanned(source)).IsEnabled);
    }

    [Theory]
    [InlineData(StartupSource.RegistryCurrentUser, "HKCU")]
    [InlineData(StartupSource.RegistryLocalMachine, "HKLM")]
    [InlineData(StartupSource.RegistryLocalMachine32, "HKLM")]
    public void ARunOnceItem_IsNotReadAsOffByARunItemOfTheSameName(StartupSource source, string hive)
    {
        // It runs at the next start-up whatever the lists say, so it must not show as off.
        foreach (var l in Lists)
            Approve(hive, l, "Probe", 3);

        var entry = ReadBack(Scanned(source, registryKey: @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"));

        Assert.True(entry.IsEnabled);
        Assert.Equal("Enabled", entry.StatusText);
    }

    [Fact]
    public void AnItemNoListNames_StaysAsTheScanFoundIt()
    {
        Approve("HKCU", "Run", "Another", 3);

        var entry = ReadBack(Scanned(StartupSource.RegistryCurrentUser));

        Assert.True(entry.IsEnabled);
        Assert.Equal("Enabled", entry.StatusText);
    }

    [Fact]
    public void AnEmptyBlob_LeavesTheItemAsTheScanFoundIt()
    {
        using (var key = _currentUser.CreateSubKey($@"{ApprovedLists}\Run"))
            key.SetValue("Probe", Array.Empty<byte>(), RegistryValueKind.Binary);

        Assert.True(ReadBack(Scanned(StartupSource.RegistryCurrentUser)).IsEnabled);
    }

    [Fact]
    public void AValueNameInAnotherCase_NamesTheSameItem()
    {
        // Registry value names are not case-sensitive, so neither is Windows' read of the list.
        Approve("HKCU", "Run", "PROBE", 3);

        Assert.False(ReadBack(Scanned(StartupSource.RegistryCurrentUser, "Probe")).IsEnabled);
    }

    [Theory]
    [InlineData(StartupSource.RegistryCurrentUser)]
    [InlineData(StartupSource.StartupFolder)]
    [InlineData(StartupSource.RegistryLocalMachine)]
    [InlineData(StartupSource.RegistryLocalMachine32)]
    [InlineData(StartupSource.CommonStartupFolder)]
    public async Task WhatASwitchWrites_TheScanReadsBack(StartupSource source)
    {
        // The two halves agree on the list and on the bit.
        Assert.True(await SetEnabledAsync(Scanned(source), false));
        Assert.False(ReadBack(Scanned(source)).IsEnabled);

        Assert.True(await SetEnabledAsync(Scanned(source), true));
        Assert.True(ReadBack(Scanned(source)).IsEnabled);
    }
}
