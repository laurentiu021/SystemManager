// SysManager · PutBackSignalTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests that each tab Undo Changes puts a change back for hears it and reads its state again (#1525), and that it
/// hears only its own change.
/// </summary>
/// <remarks>
/// Every tab is built once and kept for the session, so after a put-back from Undo Changes it went on showing the
/// change: Gaming Profile kept Start off, DNS &amp; Hosts would have saved the old entries over the restored file,
/// and the rest showed values that were no longer on the PC. Every store here is a temp folder, a throwaway registry
/// key or a stand-in.
/// </remarks>
// Serialized: the tabs here reach the process-wide dialog, activity log and operation lock services. Required by
// ArchitectureTests.ProcessWideStaticUsers_AreInTheSerializedCollection.
[Collection("ProcessWideStatics")]
public sealed class PutBackSignalTests
{
    private static IPowerShellRunner Runner()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(0);
        return runner;
    }

    [Fact]
    public async Task PerformanceMode_ReadsItsRecordAgain_WhenItsChangeIsPutBack()
    {
        var dir = Directory.CreateTempSubdirectory("PutBack_Performance_");
        try
        {
            var runner = Runner();
            using var service = new PerformanceService(runner, new RestorePointService(runner), dir.FullName);
            Assert.True(service.SaveSnapshot(new PerformanceService.OriginalSnapshot(
                "381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced", true, true, true, true, true, 5, null)));
            var gaming = Substitute.For<IGamingProfileService>();
            var signal = new PutBackSignal();
            using var vm = new PerformanceViewModel(service, gaming, signal);
            await vm.InitializationComplete;
            Assert.True(vm.HasSnapshot);

            Assert.True(service.DeleteSnapshot());
            signal.Raise(UndoChangeKind.HostsFile);
            await vm.PutBackReload;
            Assert.True(vm.HasSnapshot, "another tab's put-back is not this one's");

            signal.Raise(UndoChangeKind.PerformanceMode);
            await vm.PutBackReload;

            Assert.False(vm.HasSnapshot);
            Assert.Equal("Settings read again after Undo Changes.", vm.StatusMessage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PerformanceMode_ReadsTheSettingsUnderACommandOfItsOwn_WithoutTakingItsBusyOrItsLine()
    {
        // A read the usual way would turn Busy off while the command ran and write over its progress line. Not reading
        // them at all would leave them as they were before for a command that ends without reading them itself. The
        // record is read again too: that part decides what Restore All offers.
        var dir = Directory.CreateTempSubdirectory("PutBack_PerformanceBusy_");
        try
        {
            var runner = Runner();
            using var service = new PerformanceService(runner, new RestorePointService(runner), dir.FullName);
            Assert.True(service.SaveSnapshot(new PerformanceService.OriginalSnapshot(
                "381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced", true, true, true, true, true, 5, null)));
            var signal = new PutBackSignal();
            using var vm = new PerformanceViewModel(service, Substitute.For<IGamingProfileService>(), signal);
            await vm.InitializationComplete;

            vm.IsBusy = true;
            vm.StatusMessage = "Switching to High Performance…";
            Assert.True(service.DeleteSnapshot());
            runner.ClearReceivedCalls();
            signal.Raise(UndoChangeKind.PerformanceMode);
            await vm.PutBackReload;

            Assert.True(vm.IsBusy);
            Assert.Equal("Switching to High Performance…", vm.StatusMessage);
            Assert.False(vm.HasSnapshot);
            await runner.Received(1).RunProcessAsync("powercfg.exe", "/getactivescheme", Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PerformanceMode_ReadsTheSettingsAgain_OnceAReadThatWasRunningEnds()
    {
        // A read already running when the settings went back may have read them from before. It is not run beside —
        // that would turn Busy off under it — but after it, once.
        var dir = Directory.CreateTempSubdirectory("PutBack_PerformanceReadRunning_");
        try
        {
            var runner = Runner();
            using var service = new PerformanceService(runner, new RestorePointService(runner), dir.FullName);
            var signal = new PutBackSignal();
            using var vm = new PerformanceViewModel(service, Substitute.For<IGamingProfileService>(), signal);
            await vm.InitializationComplete;

            var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            runner.RunProcessAsync("powercfg.exe", "/getactivescheme", Arg.Any<CancellationToken>(),
                    Arg.Any<System.Text.Encoding?>())
                .Returns(held.Task, Task.FromResult(0));
            runner.ClearReceivedCalls();

            var refresh = vm.RefreshCommand.ExecuteAsync(null);
            Assert.True(vm.IsBusy);
            signal.Raise(UndoChangeKind.PerformanceMode);
            // Bounded: a read run beside the one that is held would wait behind it, and fail here rather than hang.
            await vm.PutBackReload.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("Reading performance settings…", vm.StatusMessage);

            held.SetResult(0);
            await refresh;

            Assert.Equal("Settings read again after Undo Changes.", vm.StatusMessage);
            Assert.False(vm.IsBusy);
            await runner.Received(2).RunProcessAsync("powercfg.exe", "/getactivescheme", Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PerformanceMode_UnderACommandOfItsOwn_KeepsTheCommandsLine_EvenWhenTheReadFails()
    {
        // The command says how it went; a read under it that failed is only logged.
        var dir = Directory.CreateTempSubdirectory("PutBack_PerformanceCommandRunning_");
        try
        {
            var runner = Runner();
            using var service = new PerformanceService(runner, new RestorePointService(runner), dir.FullName);
            var signal = new PutBackSignal();
            using var vm = new PerformanceViewModel(service, Substitute.For<IGamingProfileService>(), signal);
            await vm.InitializationComplete;

            runner.RunProcessAsync("powercfg.exe", "/getactivescheme", Arg.Any<CancellationToken>(),
                    Arg.Any<System.Text.Encoding?>())
                .Returns(Task.FromException<int>(new InvalidOperationException("powercfg did not answer.")));
            vm.IsBusy = true;
            vm.StatusMessage = "Switching to High Performance…";
            signal.Raise(UndoChangeKind.PerformanceMode);
            await vm.PutBackReload;

            Assert.True(vm.IsBusy);
            Assert.Equal("Switching to High Performance…", vm.StatusMessage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PerformanceMode_KeepsRestoreAll_WhenItsRecordCannotBeReadJustThen()
    {
        // A put-back that could not delete the record leaves it on disk, and Restore All is then the way to finish. A
        // read of it that failed just then says nothing about whether it is still there.
        var dir = Directory.CreateTempSubdirectory("PutBack_PerformanceRecordHeld_");
        try
        {
            var runner = Runner();
            using var service = new PerformanceService(runner, new RestorePointService(runner), dir.FullName);
            Assert.True(service.SaveSnapshot(new PerformanceService.OriginalSnapshot(
                "381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced", true, true, true, true, true, 5, null)));
            var signal = new PutBackSignal();
            using var vm = new PerformanceViewModel(service, Substitute.For<IGamingProfileService>(), signal);
            await vm.InitializationComplete;
            Assert.True(vm.HasSnapshot);

            using (File.Open(Path.Combine(dir.FullName, "performance-snapshot.json"), FileMode.Open, FileAccess.Read,
                       FileShare.None))
            {
                signal.Raise(UndoChangeKind.PerformanceMode);
                await vm.PutBackReload;
            }

            Assert.True(vm.HasSnapshot);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PerformanceMode_GoesOnSayingItsReadFailed_WhenTheReadAfterAPutBackFails()
    {
        // The signal comes whether or not the put-back worked, so the line after it says only what the tab read. A
        // read that failed has read nothing, and must not be covered by a line about Undo Changes.
        var dir = Directory.CreateTempSubdirectory("PutBack_PerformanceUnread_");
        try
        {
            var runner = Runner();
            using var service = new PerformanceService(runner, new RestorePointService(runner), dir.FullName);
            var signal = new PutBackSignal();
            using var vm = new PerformanceViewModel(service, Substitute.For<IGamingProfileService>(), signal);
            await vm.InitializationComplete;
            Assert.Equal("Settings loaded.", vm.StatusMessage);

            runner.RunProcessAsync("powercfg.exe", "/getactivescheme", Arg.Any<CancellationToken>(),
                    Arg.Any<System.Text.Encoding?>())
                .Returns(Task.FromException<int>(new InvalidOperationException("powercfg did not answer.")));
            signal.Raise(UndoChangeKind.PerformanceMode);
            await vm.PutBackReload;

            Assert.Equal("Read settings failed: powercfg did not answer.", vm.StatusMessage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GamingProfile_ReadsWhetherGameModeIsStillOn_WhenItsChangeIsPutBack()
    {
        var service = Substitute.For<IGamingProfileService>();
        service.IsActive.Returns(true);
        service.LoadLastConfig().Returns(new GamingProfile());
        var cpu = Substitute.For<ICpuAffinityService>();
        cpu.GetProcesses().Returns([]);
        var signal = new PutBackSignal();
        using var vm = new GamingProfileViewModel(service, cpu, signal);
        Assert.True(vm.IsSessionActive);

        service.IsActive.Returns(false);
        signal.Raise(UndoChangeKind.PerformanceMode);
        Assert.True(vm.IsSessionActive, "another tab's put-back is not this one's");

        signal.Raise(UndoChangeKind.GamingProfile);

        Assert.False(vm.IsSessionActive);
        Assert.Equal("Undo Changes turned game mode off.", vm.StatusMessage);
    }

    [Fact]
    public async Task DnsAndHosts_ReadsTheHostsFileAgain_WhenItWasRestored()
    {
        // Save rewrites the whole file from the list, so a list read before the restore would write the old entries
        // straight back over it.
        var dir = Directory.CreateTempSubdirectory("PutBack_Hosts_");
        try
        {
            var hostsPath = Path.Combine(dir.FullName, "hosts");
            File.WriteAllText(hostsPath, "0.0.0.0\tads.example.com\n0.0.0.0\ttracker.example.com\n");
            var signal = new PutBackSignal();
            using var vm = new DnsHostsViewModel(new DnsService(Runner()), new HostsFileService(hostsPath), signal);
            await vm.InitializationComplete;
            Assert.Equal(2, vm.HostEntries.Count);

            File.WriteAllText(hostsPath, "127.0.0.1\tlocalhost\n");
            signal.Raise(UndoChangeKind.Services);
            await vm.PutBackReload;
            Assert.Equal(2, vm.HostEntries.Count);

            signal.Raise(UndoChangeKind.HostsFile);
            await vm.PutBackReload;

            Assert.Equal("localhost", Assert.Single(vm.HostEntries).Hostname);
            Assert.Equal("Read again after Undo Changes: 1 entry.", vm.HostsStatus);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DnsAndHosts_GoesOnSayingItsReadFailed_WhenTheReadAfterAPutBackFails()
    {
        var dir = Directory.CreateTempSubdirectory("PutBack_HostsUnread_");
        try
        {
            var hostsPath = Path.Combine(dir.FullName, "hosts");
            File.WriteAllText(hostsPath, "0.0.0.0\tads.example.com\n");
            var signal = new PutBackSignal();
            using var vm = new DnsHostsViewModel(new DnsService(Runner()), new HostsFileService(hostsPath), signal);
            await vm.InitializationComplete;
            Assert.Equal("Loaded 1 entries.", vm.HostsStatus);

            // Held with no sharing, so the read after the put-back cannot open the file.
            using (new FileStream(hostsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                signal.Raise(UndoChangeKind.HostsFile);
                await vm.PutBackReload;
            }

            Assert.StartsWith("Error reading hosts file: ", vm.HostsStatus, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EnvironmentVariables_ReadsTheVariablesAgain_WhenTheyWereRestored()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "before");
        var signal = new PutBackSignal();
        using var vm = new EnvironmentVariablesViewModel(env.Service, signal);
        await vm.InitializationComplete;
        Assert.Equal("before", vm.Variables.Single(v => v.Name == "SAFE_USER").Value);

        env.SetUser("SAFE_USER", "after");
        signal.Raise(UndoChangeKind.Services);
        await vm.PutBackReload;
        Assert.Equal("before", vm.Variables.Single(v => v.Name == "SAFE_USER").Value);

        signal.Raise(UndoChangeKind.EnvironmentVariables);
        await vm.PutBackReload;

        Assert.Equal("after", vm.Variables.Single(v => v.Name == "SAFE_USER").Value);
        Assert.Equal("Variables read again after Undo Changes.", vm.StatusMessage);
    }

    [Fact]
    public async Task Services_ReadsTheListAgain_WithoutTheRefreshNotice()
    {
        // The user is on Undo Changes when this happens, where "Services refreshed" would be a notice about a list
        // they are not looking at.
        var dir = Directory.CreateTempSubdirectory("PutBack_Services_");
        var notices = new List<string>();
        try
        {
            var signal = new PutBackSignal();
            var reads = 0;
            using var vm = new ServicesViewModel(Runner(), new ServiceStartupLedgerService(dir.FullName), signal,
                (title, _) => notices.Add(title), () => { reads++; return TwoServices(); });
            await vm.InitializationComplete;
            notices.Clear();

            signal.Raise(UndoChangeKind.EnvironmentVariables);
            await vm.PutBackReload;
            Assert.Equal(1, reads);

            signal.Raise(UndoChangeKind.Services);
            await vm.PutBackReload;

            Assert.Equal(2, reads);
            Assert.Empty(notices);
            Assert.Equal("Read again after Undo Changes: 2 services (1 running).", vm.StatusMessage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Services_KeepTheNewestList_WhenAnOlderReadEndsLast()
    {
        // A Refresh and the read after a put-back can overlap. The older one ending last must not paint the services as
        // they were before the put-back.
        var dir = Directory.CreateTempSubdirectory("PutBack_ServicesOrder_");
        using var olderStarted = new ManualResetEventSlim(false);
        using var older = new ManualResetEventSlim(false);
        try
        {
            List<ServiceEntry> before =
                [new() { Name = "AlphaSvc", DisplayName = "Alpha", Status = "Stopped", StartType = "Disabled" }];
            var reads = 0;
            var signal = new PutBackSignal();
            using var vm = new ServicesViewModel(Runner(), new ServiceStartupLedgerService(dir.FullName), signal, (_, _) => { },
                () =>
                {
                    // The second read is the Refresh's, the older one: it waits, and answers with the list from before
                    // the put-back.
                    if (Interlocked.Increment(ref reads) != 2) return TwoServices();
                    olderStarted.Set();
                    // Bounded, so an assertion that throws before older.Set() cannot leave this thread parked.
                    older.Wait(TimeSpan.FromSeconds(30));
                    return before;
                });
            await vm.InitializationComplete;

            var refresh = vm.RefreshCommand.ExecuteAsync(null);
            // Waited for rather than assumed: the two reads run on the thread pool, which may start them in either order,
            // and the put-back's must be the second one to start or it is the read that waits.
            Assert.True(olderStarted.Wait(TimeSpan.FromSeconds(30)), "the older read never started");
            signal.Raise(UndoChangeKind.Services);
            await vm.PutBackReload;
            older.Set();
            await refresh;

            Assert.Equal(["Alpha", "Beta"], vm.Services.Select(s => s.DisplayName));
            Assert.Equal("Read again after Undo Changes: 2 services (1 running).", vm.StatusMessage);
            Assert.False(vm.IsBusy);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Services_GoesOnSayingItsReadFailed_WhenTheReadAfterAPutBackFails()
    {
        var dir = Directory.CreateTempSubdirectory("PutBack_ServicesUnread_");
        var unreadable = false;
        try
        {
            var signal = new PutBackSignal();
            using var vm = new ServicesViewModel(Runner(), new ServiceStartupLedgerService(dir.FullName), signal,
                (_, _) => { },
                () => unreadable ? throw new InvalidOperationException("The service list could not be read.") : TwoServices());
            await vm.InitializationComplete;
            Assert.Equal("Loaded 2 services (1 running).", vm.StatusMessage);

            unreadable = true;
            signal.Raise(UndoChangeKind.Services);
            await vm.PutBackReload;

            Assert.Equal("Service scan failed: The service list could not be read.", vm.StatusMessage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Services_StillShowTheRefreshNotice_ForARefresh()
    {
        // The other half of the quiet read above: only the put-back's read is quiet.
        var dir = Directory.CreateTempSubdirectory("PutBack_ServicesRefresh_");
        var notices = new List<string>();
        try
        {
            using var vm = new ServicesViewModel(Runner(), new ServiceStartupLedgerService(dir.FullName), new PutBackSignal(),
                (title, _) => notices.Add(title), TwoServices);
            await vm.InitializationComplete;
            notices.Clear();

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.Equal(["Services refreshed"], notices);
            Assert.Equal("Loaded 2 services (1 running).", vm.StatusMessage);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // A stand-in for the list Windows returns, so what the tab says can be pinned to the word.
    private static List<ServiceEntry> TwoServices() =>
    [
        new() { Name = "AlphaSvc", DisplayName = "Alpha", Status = "Running", StartType = "Automatic" },
        new() { Name = "BetaSvc", DisplayName = "Beta", Status = "Stopped", StartType = "Manual" },
    ];

    /// <summary>A signal that counts who is listening, so a test can see a tab let go of it.</summary>
    private sealed class CountingSignal : IPutBackSignal
    {
        private Action<UndoChangeKind>? _handlers;

        public int Listeners { get; private set; }

        public event Action<UndoChangeKind>? PutBack
        {
            add { _handlers += value; Listeners++; }
            remove { _handlers -= value; Listeners--; }
        }

        public void Raise(UndoChangeKind kind) => _handlers?.Invoke(kind);
    }

    [Fact]
    public async Task EveryTabThatListens_LetsGoOfTheSignal_WhenItIsDisposed()
    {
        // The signal is one for the whole app and outlives the tabs, so a handler left on it would keep a disposed tab
        // alive, and reacting.
        var dir = Directory.CreateTempSubdirectory("PutBack_Listeners_");
        try
        {
            var runner = Runner();
            using var env = new RedirectedEnvironment();
            using var performance = new PerformanceService(runner, new RestorePointService(runner), dir.FullName);
            var gaming = Substitute.For<IGamingProfileService>();
            gaming.LoadLastConfig().Returns(new GamingProfile());
            var cpu = Substitute.For<ICpuAffinityService>();
            cpu.GetProcesses().Returns([]);
            var signal = new CountingSignal();

            ViewModelBase[] tabs =
            [
                new PerformanceViewModel(performance, gaming, signal),
                new ServicesViewModel(runner, new ServiceStartupLedgerService(dir.FullName), signal, (_, _) => { }, TwoServices),
                new DnsHostsViewModel(new DnsService(runner), new HostsFileService(Path.Combine(dir.FullName, "hosts")), signal),
                new EnvironmentVariablesViewModel(env.Service, signal),
                new GamingProfileViewModel(gaming, cpu, signal),
            ];
            Assert.Equal(tabs.Length, signal.Listeners);

            foreach (var tab in tabs)
            {
                await tab.InitializationComplete;
                tab.Dispose();
            }

            Assert.Equal(0, signal.Listeners);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ATabThatIsDisposed_DoesNotReact()
    {
        var service = Substitute.For<IGamingProfileService>();
        service.LoadLastConfig().Returns(new GamingProfile());
        var cpu = Substitute.For<ICpuAffinityService>();
        cpu.GetProcesses().Returns([]);
        var signal = new PutBackSignal();
        var vm = new GamingProfileViewModel(service, cpu, signal);
        var heard = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(GamingProfileViewModel.StatusMessage)) heard++; };

        vm.Dispose();
        signal.Raise(UndoChangeKind.GamingProfile);

        Assert.Equal(0, heard);
    }
}
