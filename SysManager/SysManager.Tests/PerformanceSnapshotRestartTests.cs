// SysManager · PerformanceSnapshotRestartTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Regression coverage for the persisted Performance Mode recovery point.
/// Every test uses an isolated directory and never touches the real app profile.
/// </summary>
[Collection("DialogService")]
public sealed class PerformanceSnapshotRestartTests
{
    private static IPowerShellRunner NewRunner()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(0);
        return runner;
    }

    private static PerformanceService NewService(
        string configDir,
        IPowerShellRunner? runner = null)
    {
        runner ??= NewRunner();
        return new PerformanceService(runner, new RestorePointService(runner), configDir);
    }

    private static PerformanceService.OriginalSnapshot ValidSnapshot(
        DateTimeOffset? capturedAtUtc = null) =>
        new(
            PowerPlanGuid: "381b4222-f694-41f0-9685-ff5bb260df2e",
            PowerPlanName: "Balanced",
            UiEffectsEnabled: true,
            GameModeEnabled: true,
            XboxGameBarEnabled: true,
            XboxGameDvrEnabled: true,
            GpuDynamicPstate: true,
            ProcessorMinPercentAc: 5,
            NvidiaSubKey: null,
            CapturedAtUtc: capturedAtUtc);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SysManagerPerformanceTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task Initialization_WithPersistedSnapshot_EnablesRestoreAndShowsCaptureTime()
    {
        var dir = CreateTempDirectory();
        var snapshot = ValidSnapshot(
            new DateTimeOffset(2026, 7, 31, 12, 34, 0, TimeSpan.Zero)) with
        {
            XboxGameDvrEnabled = false,
            GpuDynamicPstate = false,
            NvidiaSubKey = "0000"
        };

        try
        {
            using (var writer = NewService(dir))
                Assert.True(writer.SaveSnapshot(snapshot));

            using var service = NewService(dir);
            Assert.Equal(snapshot, service.LoadSnapshot());

            using var vm = new PerformanceViewModel(service);
            await vm.InitializationComplete;

            Assert.True(vm.HasSnapshot);

            var previousDialog = DialogService.Instance;
            var dialog = Substitute.For<IDialogService>();
            dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
            DialogService.Instance = dialog;
            try
            {
                await vm.RestoreAllCommand.ExecuteAsync(null);

                dialog.Received(1).Confirm(
                    Arg.Is<string>(message =>
                        message != null
                        && message.Contains("Snapshot captured:", StringComparison.Ordinal)
                        && message.Contains("2026", StringComparison.Ordinal)
                        && message.Contains("Game DVR → OFF", StringComparison.Ordinal)
                        && message.Contains("GPU → Max performance", StringComparison.Ordinal)),
                    "Restore Original Settings — Confirm");
                Assert.DoesNotContain("Nothing to restore", vm.StatusMessage, StringComparison.Ordinal);
            }
            finally
            {
                DialogService.Instance = previousDialog;
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Initialization_PersistedSnapshotIsAvailableBeforeBlockedRefreshCompletes()
    {
        var dir = CreateTempDirectory();
        var refreshStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var snapshot = ValidSnapshot() with
            {
                NvidiaSubKey = "0000"
            };
            using (var writer = NewService(dir))
                Assert.True(writer.SaveSnapshot(snapshot));

            var runner = Substitute.For<IPowerShellRunner>();
            runner.RunProcessAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<System.Text.Encoding?>())
                .Returns(_ =>
                {
                    refreshStarted.TrySetResult(true);
                    return releaseRefresh.Task;
                });

            using var service = NewService(dir, runner);
            using var vm = new PerformanceViewModel(service);
            try
            {
                await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // Restore hydration precedes the blocked live probe, so recovery is immediately visible.
                Assert.True(vm.HasSnapshot);

                var previousDialog = DialogService.Instance;
                var dialog = Substitute.For<IDialogService>();
                dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
                DialogService.Instance = dialog;
                try
                {
                    // A missing live profile cannot suppress the conservative GPU reboot warning.
                    await vm.RestoreAllCommand.ExecuteAsync(null);

                    dialog.Received(1).Confirm(
                        Arg.Is<string>(message =>
                            message != null
                            && message.Contains(
                                "GPU → Dynamic P-state (reboot needed)",
                                StringComparison.Ordinal)),
                        "Restore Original Settings — Confirm");
                }
                finally
                {
                    DialogService.Instance = previousDialog;
                }
            }
            finally
            {
                releaseRefresh.TrySetResult(0);
                await vm.InitializationComplete.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            releaseRefresh.TrySetResult(0);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Initialization_WithLegacySnapshot_EnablesRestoreWithUnknownCaptureTime()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "performance-snapshot.json"),
                """
                {
                  "PowerPlanGuid": "381b4222-f694-41f0-9685-ff5bb260df2e",
                  "PowerPlanName": "Balanced",
                  "UiEffectsEnabled": true,
                  "GameModeEnabled": true,
                  "XboxGameBarEnabled": true,
                  "XboxGameDvrEnabled": true,
                  "GpuDynamicPstate": true,
                  "ProcessorMinPercentAc": 5,
                  "NvidiaSubKey": null
                }
                """);

            using var service = NewService(dir);
            using var vm = new PerformanceViewModel(service);
            await vm.InitializationComplete;

            Assert.True(vm.HasSnapshot);
            Assert.Null(service.LoadSnapshot()!.CapturedAtUtc);

            var previousDialog = DialogService.Instance;
            var dialog = Substitute.For<IDialogService>();
            dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
            DialogService.Instance = dialog;
            try
            {
                await vm.RestoreAllCommand.ExecuteAsync(null);

                dialog.Received(1).Confirm(
                    Arg.Is<string>(message =>
                        message != null
                        && message.Contains(
                            "Unknown (snapshot created by an earlier SysManager version)",
                            StringComparison.Ordinal)),
                    "Restore Original Settings — Confirm");
            }
            finally
            {
                DialogService.Instance = previousDialog;
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreFailure_PreservesPersistedSnapshotForRetry()
    {
        var dir = CreateTempDirectory();
        var runner = NewRunner();

        try
        {
            using var service = NewService(dir, runner);
            Assert.True(service.SaveSnapshot(ValidSnapshot()));

            using var vm = new PerformanceViewModel(service);
            await vm.InitializationComplete;
            Assert.True(vm.HasSnapshot);

            runner.RunProcessAsync(
                    "powercfg.exe",
                    Arg.Is<string>(arguments =>
                        arguments != null
                        && arguments.StartsWith("/setactive ", StringComparison.Ordinal)),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<System.Text.Encoding?>())
                .Returns(5);

            var previousDialog = DialogService.Instance;
            var dialog = Substitute.For<IDialogService>();
            dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
            DialogService.Instance = dialog;
            try
            {
                await vm.RestoreAllCommand.ExecuteAsync(null);

                Assert.True(vm.HasSnapshot);
                Assert.NotNull(service.LoadSnapshot());
                Assert.Contains("failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                DialogService.Instance = previousDialog;
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadSnapshot_MissingRequiredProperty_ReturnsNull()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "performance-snapshot.json"),
                """
                {
                  "PowerPlanName": "Balanced",
                  "UiEffectsEnabled": true,
                  "GameModeEnabled": true,
                  "XboxGameBarEnabled": true,
                  "XboxGameDvrEnabled": true,
                  "GpuDynamicPstate": true,
                  "ProcessorMinPercentAc": 5,
                  "NvidiaSubKey": null
                }
                """);

            using var service = NewService(dir);
            Assert.Null(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadSnapshot_WrongPropertyType_ReturnsNull()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "performance-snapshot.json"),
                """
                {
                  "PowerPlanGuid": "381b4222-f694-41f0-9685-ff5bb260df2e",
                  "PowerPlanName": "Balanced",
                  "UiEffectsEnabled": "yes",
                  "GameModeEnabled": true,
                  "XboxGameBarEnabled": true,
                  "XboxGameDvrEnabled": true,
                  "GpuDynamicPstate": true,
                  "ProcessorMinPercentAc": 5,
                  "NvidiaSubKey": null
                }
                """);

            using var service = NewService(dir);
            Assert.Null(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadSnapshot_DuplicateProperty_ReturnsNull()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "performance-snapshot.json"),
                """
                {
                  "PowerPlanGuid": "381b4222-f694-41f0-9685-ff5bb260df2e",
                  "PowerPlanName": "Balanced",
                  "PowerPlanName": "Spoofed",
                  "UiEffectsEnabled": true,
                  "GameModeEnabled": true,
                  "XboxGameBarEnabled": true,
                  "XboxGameDvrEnabled": true,
                  "GpuDynamicPstate": true,
                  "ProcessorMinPercentAc": 5,
                  "NvidiaSubKey": null
                }
                """);

            using var service = NewService(dir);
            Assert.Null(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadSnapshot_OversizedFile_ReturnsNull()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "performance-snapshot.json"),
                new string('x', PerformanceService.MaxSnapshotBytes + 1));

            using var service = NewService(dir);
            Assert.Null(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveSnapshot_InvalidSnapshot_ReturnsFalseAndWritesNothing()
    {
        var dir = CreateTempDirectory();
        try
        {
            using var service = NewService(dir);
            var invalid = ValidSnapshot() with { ProcessorMinPercentAc = 101 };

            Assert.False(service.SaveSnapshot(invalid));
            Assert.False(File.Exists(Path.Combine(dir, "performance-snapshot.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
