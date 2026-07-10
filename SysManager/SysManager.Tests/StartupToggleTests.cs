// SysManager · StartupToggleTests — tests for SetEnabled toggle logic
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="StartupService.SetEnabledAsync"/> covering registry-based
/// and Task Scheduler entries, plus specific error messages (#159, #160).
/// </summary>
public class StartupToggleTests
{
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

        var result = await StartupService.SetEnabledAsync(entry, false);

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

        var result = await StartupService.SetEnabledAsync(entry, false);

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

        var result = await StartupService.SetEnabledAsync(entry, false);

        Assert.DoesNotContain("may need admin", entry.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetEnabledAsync_TaskScheduler_WithBogusPath_ReturnsFalseWithSpecificError()
    {
        var entry = new StartupEntry
        {
            Name = "BogusTask",
            Command = "bogus.exe",
            Source = StartupSource.TaskScheduler,
            TaskPath = @"\NonExistent\BogusTask_" + Guid.NewGuid().ToString("N"),
            IsEnabled = true,
            StatusText = "Enabled"
        };

        var result = await StartupService.SetEnabledAsync(entry, false);

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

        await StartupService.SetEnabledAsync(entry, false);

        Assert.True(entry.IsEnabled);
    }

    // ---------- RunOnce entries can't be toggled via StartupApproved (P2 #35) ----------

    [Theory]
    [InlineData(StartupSource.RegistryCurrentUser)]
    [InlineData(StartupSource.RegistryLocalMachine)]
    public async Task SetEnabledAsync_RunOnce_ReturnsFalseWithHonestMessage_AndDoesNotDisable(StartupSource source)
    {
        // Regression (P2 #35): Windows has no StartupApproved\RunOnce and never consults
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

        var result = await StartupService.SetEnabledAsync(entry, false);

        Assert.False(result);
        Assert.Contains("run-once", entry.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(entry.IsEnabled); // never flipped to disabled
    }
}
