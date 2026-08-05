// SysManager · ServicesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ServicesViewModel"/> — filter logic, property defaults,
/// and command existence. Uses reflection to inject test data into the private
/// _allServices field to test ApplyFilter without hitting real WMI.
/// <para>Seeding goes through <c>CreateWithDataAsync</c>, which waits for the view model's
/// initialization to finish first — see the comment there for why the order matters. The
/// constructor tests below build the view model directly and assert only on defaults, so
/// they neither need nor use the helper.</para>
/// </summary>
public class ServicesViewModelTests
{
    private static readonly List<ServiceEntry> TestServices = new()
    {
        new() { Name = "wuauserv", DisplayName = "Windows Update", Description = "Manages Windows updates", Status = "Running", StartType = "Automatic", Recommendation = "keep-enabled", SafetyLevel = Models.SafetyLevel.Caution },
        new() { Name = "Spooler", DisplayName = "Print Spooler", Description = "Manages print jobs", Status = "Running", StartType = "Automatic", Recommendation = "safe-to-disable", SafetyLevel = Models.SafetyLevel.Caution },
        new() { Name = "XboxGipSvc", DisplayName = "Xbox Accessory Management", Description = "Manages Xbox accessories", Status = "Stopped", StartType = "Manual", Recommendation = "safe-to-disable", SafetyLevel = Models.SafetyLevel.Safe },
        new() { Name = "WSearch", DisplayName = "Windows Search", Description = "Provides content indexing", Status = "Running", StartType = "Automatic", Recommendation = "advanced", SafetyLevel = Models.SafetyLevel.Caution },
        new() { Name = "BITS", DisplayName = "Background Intelligent Transfer", Description = "Transfers files in background", Status = "Stopped", StartType = "Manual", Recommendation = "keep-enabled", SafetyLevel = Models.SafetyLevel.Critical },
    };

    private static async Task<ServicesViewModel> CreateWithDataAsync(List<ServiceEntry>? services = null)
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner());

        // Wait for initialization BEFORE seeding. The constructor starts InitAsync, whose
        // RefreshAsync does `_allServices = await Task.Run(ServiceManagerService.GetAllServices)`
        // — it assigns the same field this helper seeds, after an await, so a value written
        // during that window is replaced by the real service list. Measured against the live
        // view model: seeding first, the seed was overwritten 25 out of 25 times, with three
        // fixtures replaced by the machine's 320 actual services.
        //
        // The tests pass today only because they read `Services` synchronously, right after
        // ApplyFilter and before the load lands. Any test that awaits before asserting would
        // read the runner's real services instead of the fixtures, and the failure would look
        // like a filtering bug rather than an ordering one. Awaiting first makes the data the
        // test controls, deterministically and without a sleep.
        await vm.InitializationComplete;

        var field = typeof(ServicesViewModel).GetField("_allServices", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(vm, services ?? TestServices);

        // Trigger ApplyFilter so the Services collection reflects the injected data.
        var applyFilter = typeof(ServicesViewModel).GetMethod("ApplyFilter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        applyFilter.Invoke(vm, null);
        return vm;
    }

    // ── Constructor / Defaults ──

    [Fact]
    public void Constructor_Collections_NotNull()
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        Assert.NotNull(vm.Services);
    }

    [Fact]
    public void Constructor_FilterOptions_ContainsExpected()
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        Assert.Contains("All", vm.FilterOptions);
        Assert.Contains("Running", vm.FilterOptions);
        Assert.Contains("Stopped", vm.FilterOptions);
        Assert.Contains("Safe", vm.FilterOptions);
        Assert.Contains("Caution", vm.FilterOptions);
        Assert.Contains("Critical", vm.FilterOptions);
    }

    [Fact]
    public void Constructor_DefaultFilter_Empty()
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        Assert.Equal("", vm.FilterText);
    }

    [Fact]
    public void Constructor_DefaultSelectedFilter_All()
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        Assert.Equal("All", vm.SelectedFilter);
    }

    [Fact]
    public void Constructor_Commands_Exist()
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.StartServiceCommand);
        Assert.NotNull(vm.StopServiceCommand);
        Assert.NotNull(vm.DisableServiceCommand);
        Assert.NotNull(vm.EnableServiceCommand);
        Assert.NotNull(vm.ToggleHighlightCommand);
    }

    // ── ApplyFilter: category filters ──

    [Fact]
    public async Task ApplyFilter_All_ShowsAllServices()
    {
        var vm = await CreateWithDataAsync();
        vm.SelectedFilter = "All";
        Assert.Equal(5, vm.Services.Count);
    }

    [Fact]
    public async Task ApplyFilter_Running_ShowsOnlyRunning()
    {
        var vm = await CreateWithDataAsync();
        vm.SelectedFilter = "Running";
        Assert.All(vm.Services, s => Assert.Equal("Running", s.Status));
        Assert.Equal(3, vm.Services.Count);
    }

    [Fact]
    public async Task ApplyFilter_Stopped_ShowsOnlyStopped()
    {
        var vm = await CreateWithDataAsync();
        vm.SelectedFilter = "Stopped";
        Assert.All(vm.Services, s => Assert.Equal("Stopped", s.Status));
        Assert.Equal(2, vm.Services.Count);
    }

    [Fact]
    public async Task ApplyFilter_SafeLevel_ShowsOnlySafe()
    {
        var vm = await CreateWithDataAsync();
        vm.SelectedFilter = "Safe";
        Assert.All(vm.Services, s => Assert.Equal(Models.SafetyLevel.Safe, s.SafetyLevel));
        Assert.Single(vm.Services);
    }

    [Fact]
    public async Task ApplyFilter_Safe_ShowsOnlySafe()
    {
        var vm = await CreateWithDataAsync();
        vm.SelectedFilter = "Safe";
        Assert.All(vm.Services, s => Assert.Equal(Models.SafetyLevel.Safe, s.SafetyLevel));
    }

    // ── ApplyFilter: text filter ──

    [Fact]
    public async Task ApplyFilter_TextFilter_MatchesDisplayName()
    {
        var vm = await CreateWithDataAsync();
        vm.FilterText = "Print";
        Assert.Single(vm.Services);
        Assert.Equal("Print Spooler", vm.Services[0].DisplayName);
    }

    [Fact]
    public async Task ApplyFilter_TextFilter_MatchesServiceName()
    {
        var vm = await CreateWithDataAsync();
        vm.FilterText = "wuauserv";
        Assert.Single(vm.Services);
        Assert.Equal("Windows Update", vm.Services[0].DisplayName);
    }

    [Fact]
    public async Task ApplyFilter_TextFilter_MatchesDescription()
    {
        var vm = await CreateWithDataAsync();
        vm.FilterText = "indexing";
        Assert.Single(vm.Services);
        Assert.Equal("Windows Search", vm.Services[0].DisplayName);
    }

    [Fact]
    public async Task ApplyFilter_TextFilter_CaseInsensitive()
    {
        var vm = await CreateWithDataAsync();
        vm.FilterText = "XBOX";
        Assert.Single(vm.Services);
        Assert.Equal("Xbox Accessory Management", vm.Services[0].DisplayName);
    }

    [Fact]
    public async Task ApplyFilter_TextFilter_NoMatch_ReturnsEmpty()
    {
        var vm = await CreateWithDataAsync();
        vm.FilterText = "zzz_nonexistent_zzz";
        Assert.Empty(vm.Services);
    }

    // ── ApplyFilter: combined text + category ──

    [Fact]
    public async Task ApplyFilter_TextAndCategory_Combined()
    {
        var vm = await CreateWithDataAsync();
        vm.SelectedFilter = "Running";
        vm.FilterText = "Update";
        Assert.Single(vm.Services);
        Assert.Equal("Windows Update", vm.Services[0].DisplayName);
    }

    [Fact]
    public async Task ApplyFilter_TextAndCategory_NoOverlap_Empty()
    {
        var vm = await CreateWithDataAsync();
        vm.SelectedFilter = "Stopped";
        vm.FilterText = "Windows Update";
        Assert.Empty(vm.Services);
    }

    // ── ApplyFilter: sorting ──

    [Fact]
    public async Task ApplyFilter_SortsByDisplayName()
    {
        var vm = await CreateWithDataAsync();
        vm.SelectedFilter = "All";
        var names = vm.Services.Select(s => s.DisplayName).ToList();
        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, names);
    }

    // ── ApplyFilter: empty data ──

    [Fact]
    public async Task ApplyFilter_EmptyList_NoException()
    {
        var vm = await CreateWithDataAsync(new List<ServiceEntry>());
        vm.SelectedFilter = "Running";
        Assert.Empty(vm.Services);
    }

    // ── Property change triggers filter ──

    [Fact]
    public async Task SelectedFilter_Change_TriggersRefilter()
    {
        var vm = await CreateWithDataAsync();
        vm.SelectedFilter = "All";
        Assert.Equal(5, vm.Services.Count);
        vm.SelectedFilter = "Stopped";
        Assert.Equal(2, vm.Services.Count);
    }

    [Fact]
    public async Task Filter_Change_TriggersRefilter()
    {
        var vm = await CreateWithDataAsync();
        vm.SelectedFilter = "All";
        Assert.Equal(5, vm.Services.Count);
        vm.FilterText = "Xbox";
        Assert.Single(vm.Services);
    }

    // ── DisableService: boot-critical guard (regression) ──

    [Fact]
    public async Task DisableService_CriticalService_IsRefusedAndNotMutated()
    {
        // A Critical service (e.g. BITS in the test data, or RpcSs/DcomLaunch in
        // production) must never be disabled — disabling a boot-critical service can
        // prevent Windows from starting. The command must short-circuit with a refusal
        // message before any elevation/confirm/PowerShell call.
        var critical = new ServiceEntry
        {
            Name = "RpcSs",
            DisplayName = "Remote Procedure Call (RPC)",
            Status = "Running",
            StartType = "Automatic",
            SafetyLevel = Models.SafetyLevel.Critical,
            SafetyDescription = "Core Windows IPC. System will not function without it."
        };
        var vm = await CreateWithDataAsync(new List<ServiceEntry> { critical });

        await vm.DisableServiceCommand.ExecuteAsync(critical);

        Assert.Contains("cannot be disabled", vm.StatusMessage);
        // The entry's startup type must be untouched by the refused command.
        Assert.Equal("Automatic", critical.StartType);
    }

    [Fact]
    public async Task DisableService_NullEntry_DoesNotThrow()
    {
        var vm = await CreateWithDataAsync();
        var ex = await Record.ExceptionAsync(() => vm.DisableServiceCommand.ExecuteAsync(null));
        Assert.Null(ex);
    }

    // ── StopService: boot-critical guard (regression) ──

    [Fact]
    public async Task StopService_CriticalService_IsRefusedAndNotMutated()
    {
        // Stopping a boot/logon-critical service (RpcSs, DcomLaunch, …) is as dangerous
        // as disabling it — it can freeze the session or force a reboot. Stop must refuse
        // a Critical service outright, before any elevation/confirm/PowerShell call, and
        // leave its running state untouched (mirrors the Disable-Critical guard).
        var critical = new ServiceEntry
        {
            Name = "RpcSs",
            DisplayName = "Remote Procedure Call (RPC)",
            Status = "Running",
            StartType = "Automatic",
            SafetyLevel = Models.SafetyLevel.Critical,
            SafetyDescription = "Core Windows IPC. System will not function without it."
        };
        var vm = await CreateWithDataAsync(new List<ServiceEntry> { critical });

        await vm.StopServiceCommand.ExecuteAsync(critical);

        Assert.Contains("cannot be stopped", vm.StatusMessage);
        // The service must be left running — the refused command never touched it.
        Assert.Equal("Running", critical.Status);
    }

    [Fact]
    public async Task StopService_NullEntry_DoesNotThrow()
    {
        var vm = await CreateWithDataAsync();
        var ex = await Record.ExceptionAsync(() => vm.StopServiceCommand.ExecuteAsync(null));
        Assert.Null(ex);
    }

    // ── Startup-type ledger rehydration (regression) ──

    /// <summary>
    /// Runs the private <c>RehydratePreviousStartTypes</c> against a seeded <c>_allServices</c>,
    /// which is what <c>RefreshAsync</c> does after every scan.
    /// </summary>
    private static async Task<ServicesViewModel> CreateWithLedgerAsync(
        List<ServiceEntry> services, ServiceStartupLedgerService ledger)
    {
        var vm = new ServicesViewModel(new Services.PowerShellRunner(), ledger);
        await vm.InitializationComplete;

        typeof(ServicesViewModel)
            .GetField("_allServices", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, services);
        typeof(ServicesViewModel)
            .GetMethod("RehydratePreviousStartTypes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, null);
        return vm;
    }

    [Fact]
    public async Task Refresh_RestoresThePreviousStartTypeFromTheLedger()
    {
        // The bug: GetAllServices builds brand-new ServiceEntry objects on every scan, so the
        // in-memory PreviousStartType set by Disable was gone by the next Refresh. Enable then hit
        // StartTypeToScToken's "demand" fallback and brought an Automatic service back as Manual,
        // reporting success the whole time.
        using var temp = new TempLedgerDir();
        var ledger = temp.NewLedger();
        ledger.Remember("wuauserv", "Automatic", DateTimeOffset.UnixEpoch);

        var scanned = new List<ServiceEntry>
        {
            // As Windows reports it after the disable: Disabled, and with no memory of what it was.
            new() { Name = "wuauserv", DisplayName = "Windows Update", Status = "Stopped", StartType = "Disabled" },
        };
        using var vm = await CreateWithLedgerAsync(scanned, ledger);

        Assert.Equal("Automatic", scanned[0].PreviousStartType);
    }

    [Fact]
    public async Task Refresh_DoesNotRehydrateAServiceWindowsReportsAsEnabled()
    {
        // If the user re-enabled the service outside SysManager, the machine is the authority. A
        // stale ledger entry must not overwrite what Windows currently reports.
        using var temp = new TempLedgerDir();
        var ledger = temp.NewLedger();
        ledger.Remember("wuauserv", "Automatic", DateTimeOffset.UnixEpoch);

        var scanned = new List<ServiceEntry>
        {
            new() { Name = "wuauserv", DisplayName = "Windows Update", Status = "Running", StartType = "Manual" },
        };
        using var vm = await CreateWithLedgerAsync(scanned, ledger);

        Assert.Null(scanned[0].PreviousStartType);
    }

    [Fact]
    public async Task Refresh_LeavesServicesWithNoLedgerEntryAlone()
    {
        using var temp = new TempLedgerDir();
        var ledger = temp.NewLedger();
        ledger.Remember("wuauserv", "Automatic", DateTimeOffset.UnixEpoch);

        var scanned = new List<ServiceEntry>
        {
            new() { Name = "Spooler", DisplayName = "Print Spooler", Status = "Stopped", StartType = "Disabled" },
        };
        using var vm = await CreateWithLedgerAsync(scanned, ledger);

        // Null, not "Manual": Enable's own fallback decides that, so the ledger must not fake it.
        Assert.Null(scanned[0].PreviousStartType);
    }

    [Fact]
    public async Task Refresh_WithAnEmptyLedger_ChangesNothing()
    {
        using var temp = new TempLedgerDir();

        var scanned = new List<ServiceEntry>
        {
            new() { Name = "wuauserv", DisplayName = "Windows Update", Status = "Stopped", StartType = "Disabled" },
        };
        using var vm = await CreateWithLedgerAsync(scanned, temp.NewLedger());

        Assert.Null(scanned[0].PreviousStartType);
    }

    [Fact]
    public async Task Refresh_MatchesTheLedgerCaseInsensitively()
    {
        // Service-name casing is not guaranteed identical between the ledger write and a later scan.
        using var temp = new TempLedgerDir();
        var ledger = temp.NewLedger();
        ledger.Remember("WuauServ", "Automatic", DateTimeOffset.UnixEpoch);

        var scanned = new List<ServiceEntry>
        {
            new() { Name = "wuauserv", DisplayName = "Windows Update", Status = "Stopped", StartType = "disabled" },
        };
        using var vm = await CreateWithLedgerAsync(scanned, ledger);

        Assert.Equal("Automatic", scanned[0].PreviousStartType);
    }

    /// <summary>A throwaway ledger directory, so the developer's real %LOCALAPPDATA% file is untouched.</summary>
    private sealed class TempLedgerDir : IDisposable
    {
        private readonly string _dir;

        public TempLedgerDir()
        {
            _dir = Path.Combine(Path.GetTempPath(), "SysManagerServicesVmTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public ServiceStartupLedgerService NewLedger() => new(_dir);

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* a leftover temp dir must never fail a test run */ }
        }
    }
}
