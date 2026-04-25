// SysManager · StartupToggleTests — tests for SetEnabled toggle logic
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="StartupService.SetEnabled"/> covering registry-based
/// and Task Scheduler entries, plus specific error messages (#159, #160).
/// </summary>
public class StartupToggleTests
{
    [Fact]
    public void SetEnabled_TaskScheduler_EmptyPath_ReturnsFalseWithMessage()
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

        var result = StartupService.SetEnabled(entry, false);

        Assert.False(result);
        Assert.Contains("task path unknown", entry.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetEnabled_TaskScheduler_NullPath_ReturnsFalseWithMessage()
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

        var result = StartupService.SetEnabled(entry, false);

        Assert.False(result);
        Assert.Contains("task path unknown", entry.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetEnabled_RegistryEntry_NeverShowsGenericAdminMessage()
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

        var result = StartupService.SetEnabled(entry, false);

        Assert.DoesNotContain("may need admin", entry.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetEnabled_TaskScheduler_WithBogusPath_ReturnsFalseWithSpecificError()
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

        var result = StartupService.SetEnabled(entry, false);

        Assert.False(result);
        Assert.StartsWith("Error", entry.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("may need admin", entry.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetEnabled_PreservesEntryState_OnFailure()
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

        StartupService.SetEnabled(entry, false);

        Assert.True(entry.IsEnabled);
    }
}
