// SysManager · ServicesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using NSubstitute;
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
// Serialized: the Enable-confirm tests swap the static DialogService.Instance, which is
// process-wide shared state. Required by ArchitectureTests.DialogServiceSwappers_AreInTheSerializedCollection.
[Collection("DialogService")]
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

    // ── ApplyFilter: gaming recommendation ──
    // A DIFFERENT dataset from the Safe/Caution/Critical safety level above: safety answers "will
    // this break Windows", the recommendation answers "is this worth turning off for games, and
    // why". Both were computed per service, but only the safety level was filterable — so README's
    // "filter by … recommendation level" claim was untrue.

    [Theory]
    [InlineData("Safe to disable", "safe-to-disable")]
    [InlineData("Keep enabled", "keep-enabled")]
    [InlineData("Advanced", "advanced")]
    public async Task ApplyFilter_Recommendation_ShowsOnlyThatRecommendation(string option, string stored)
    {
        var vm = await CreateWithDataAsync();

        vm.SelectedFilter = option;

        Assert.NotEmpty(vm.Services);   // a filter matching nothing would vacuously pass Assert.All
        Assert.All(vm.Services, s => Assert.Equal(stored, s.Recommendation));
    }

    [Fact]
    public async Task ApplyFilter_Recommendation_IsNotTheSafetyLevel()
    {
        // Discriminating assertion: "Safe to disable" must not collapse into the Safe safety level.
        // In the fixture, Print Spooler is safe-to-disable but its SAFETY level is Caution — so a
        // filter that confused the two datasets would drop it.
        var vm = await CreateWithDataAsync();

        vm.SelectedFilter = "Safe to disable";

        Assert.Contains(vm.Services, s => s.Name == "Spooler");
        Assert.Contains(vm.Services, s => s.SafetyLevel == Models.SafetyLevel.Caution);
    }

    // ── Every filter must be SELECTABLE, not merely implemented ───────────────────────────────────
    //
    // The tests above prove all nine filters WORK. They passed the whole time five of them had no control
    // in the view: Running, Stopped and the three gaming recommendations could only be reached by
    // assigning SelectedFilter from code, exactly as these tests do. Meanwhile the README promised
    // filtering "by status (Running/Stopped)" and "gaming recommendation".
    //
    // That gap can only be closed against the markup — a view-model test cannot see a missing chip. Same
    // class as the ICommand reachability ratchet in ArchitectureTests, which does not cover filter values.

    [Fact]
    public void EveryFilterOption_HasAChipInTheView()
    {
        var xaml = System.Xml.Linq.XDocument.Load(ViewPath("ServicesView.xaml"));

        // The parameter each chip feeds the IsEqual converter IS the filter value it selects.
        var chipValues = xaml.Descendants()
            .Select(e => (string?)e.Attribute("IsChecked") ?? "")
            .Where(v => v.Contains("SelectedFilter", StringComparison.Ordinal)
                     && v.Contains("ConverterParameter=", StringComparison.Ordinal))
            .Select(v => v.Split("ConverterParameter=")[1].TrimEnd('}').Trim().Trim('\''))
            .ToList();

        Assert.NotEmpty(chipValues);   // else this would pass by finding nothing

        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        var missing = vm.FilterOptions.Except(chipValues, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "These filters are implemented in ApplyFilter but no control in ServicesView selects them, " +
            "so a user cannot reach them: " + string.Join(", ", missing));

        // And the reverse: a chip for a value ApplyFilter does not handle would silently show everything.
        var unknown = chipValues.Except(vm.FilterOptions, StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0,
            "These chips select a value ApplyFilter does not handle, so they fall through to the " +
            "unfiltered default: " + string.Join(", ", unknown));
    }

    [Fact]
    public async Task EveryChipCount_ReportsWhatItsFilterWouldMatch()
    {
        // Each chip shows its own count, so the count has to agree with the filter beside it — a chip
        // reading "(0)" next to a filter that would return rows is its own small lie. Counted over the
        // whole list rather than the filtered view, so selecting one chip does not zero the others.
        var vm = await CreateWithDataAsync();

        // Fixture: Running = wuauserv, Spooler, WSearch (3); Stopped = XboxGipSvc, BITS (2);
        // safe-to-disable = Spooler, XboxGipSvc (2); keep-enabled = wuauserv, BITS (2); advanced = WSearch (1).
        //
        // Total and Running are asserted alongside the rest because they are computed alongside the rest.
        // They used to be assigned in ApplyFilterCore, one caller up, so every other path into ApplyFilter
        // refreshed seven counts and left these two behind — visible here as the machine's real service
        // count sitting next to a five-entry fixture.
        Assert.Equal(5, vm.TotalCount);
        Assert.Equal(3, vm.RunningCount);
        Assert.Equal(2, vm.StoppedCount);
        Assert.Equal(2, vm.SafeToDisableCount);
        Assert.Equal(2, vm.KeepEnabledCount);
        Assert.Equal(1, vm.AdvancedCount);

        // Each count equals what its filter actually returns.
        foreach (var (option, expected) in new[]
                 {
                     ("Running", vm.RunningCount), ("Stopped", vm.StoppedCount),
                     ("Safe to disable", vm.SafeToDisableCount),
                     ("Keep enabled", vm.KeepEnabledCount), ("Advanced", vm.AdvancedCount),
                 })
        {
            vm.SelectedFilter = option;
            Assert.Equal(expected, vm.Services.Count);
        }
    }

    [Fact]
    public async Task StoppedCount_IsNotTotalMinusRunning()
    {
        // Windows also reports StartPending / StopPending / Paused, so Running and Stopped are NOT
        // complements. Deriving Stopped by subtraction would over-count the moment a service is
        // mid-transition — the chip would promise more rows than the filter returns.
        var entries = new List<ServiceEntry>
        {
            new() { Name = "a", DisplayName = "A", Description = "", Status = "Running", StartType = "Automatic", Recommendation = "", SafetyLevel = Models.SafetyLevel.Safe },
            new() { Name = "b", DisplayName = "B", Description = "", Status = "Stopped", StartType = "Manual", Recommendation = "", SafetyLevel = Models.SafetyLevel.Safe },
            new() { Name = "c", DisplayName = "C", Description = "", Status = "StartPending", StartType = "Automatic", Recommendation = "", SafetyLevel = Models.SafetyLevel.Safe },
        };
        var vm = await CreateWithDataAsync(entries);

        Assert.Equal(3, vm.TotalCount);
        Assert.Equal(1, vm.RunningCount);
        Assert.Equal(1, vm.StoppedCount);          // not 2 — StartPending is neither
        Assert.NotEqual(vm.TotalCount - vm.RunningCount, vm.StoppedCount);

        vm.SelectedFilter = "Stopped";
        Assert.Single(vm.Services);                // and the filter agrees with the count
    }

    /// <summary>The app's Views folder — .xaml is not copied to the test output.</summary>
    private static string ViewPath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "SysManager", "Views", fileName);
        Assert.True(File.Exists(path), $"{fileName} not found at {path}");
        return path;
    }

    [Fact]
    public void FilterOptions_CoverEveryRecommendationTheGuideProduces()
    {
        // Mechanical guard: GamingGuide uses exactly three recommendation values. If a fourth is ever
        // added, this fails rather than the new one silently having no filter — the class of bug that
        // left these 25 explanations unreachable in the first place.
        var produced = ServiceManagerService.GamingGuide.Values
            .Select(v => v.Rec)
            .Distinct()
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["advanced", "keep-enabled", "safe-to-disable"], produced);

        var vm = new ServicesViewModel(new Services.PowerShellRunner());
        Assert.Contains("Safe to disable", vm.FilterOptions);
        Assert.Contains("Keep enabled", vm.FilterOptions);
        Assert.Contains("Advanced", vm.FilterOptions);
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

    // ── Enable must announce the change ─────────────────────────────────────────────────────────
    //
    // Start, Stop and Disable each call DialogService.Instance.Confirm. Enable was the one mutating
    // command on this tab that did not, and its button renders on every row with no Visibility or
    // CanExecute guard — one click to a persistent, machine-scope service change.
    //
    // Every assertion below holds whether or not the run is elevated. EnableServiceAsync returns at
    // the elevation gate when not elevated and at the declined confirm when it is; neither path may
    // reach sc.exe, which is what "did the startup type change?" measures. A test that only passed
    // while elevated would be quarantined in CI, so the prompt-wording assertions skip explicitly
    // when no prompt was reached rather than asserting something they cannot observe.

    [Fact]
    public async Task EnableService_WhenConfirmDeclined_ChangesNothing()
    {
        using var temp = new TempLedgerDir();
        var ledger = temp.NewLedger();
        ledger.Remember("Spooler", "Automatic", DateTimeOffset.UnixEpoch);

        var scanned = new List<ServiceEntry>
        {
            new() { Name = "Spooler", DisplayName = "Print Spooler", Status = "Stopped", StartType = "Disabled" },
        };
        using var vm = await CreateWithLedgerAsync(scanned, ledger);
        using var dialog = new DialogAnswer(confirm: false);

        await vm.EnableServiceCommand.ExecuteAsync(scanned[0]);

        // Nothing applied: the entry still reads Disabled and the ledger still remembers the type,
        // so a later accepted Enable can still restore it rather than falling back to Manual.
        Assert.Equal("Disabled", scanned[0].StartType);
        Assert.Equal("Automatic", ledger.PreviousStartTypeFor("Spooler"));
    }

    [Fact]
    public async Task EnableService_WithNoRememberedType_SaysItWillBeSetToManual()
    {
        // The wording carries the weight. With no ledger entry — a service the user disabled outside
        // SysManager — `previous` is null and StartTypeToScToken's `_ => "demand"` fallback sets the
        // service to MANUAL. A prompt saying "restored" would describe an action the app does not
        // perform, which is the failure this guard exists to prevent.
        using var temp = new TempLedgerDir();
        var ledger = temp.NewLedger();   // deliberately empty

        var scanned = new List<ServiceEntry>
        {
            new() { Name = "WSearch", DisplayName = "Windows Search", Status = "Stopped", StartType = "Disabled" },
        };
        using var vm = await CreateWithLedgerAsync(scanned, ledger);
        using var dialog = new DialogAnswer(confirm: false);

        await vm.EnableServiceCommand.ExecuteAsync(scanned[0]);

        if (dialog.Calls == 0) return;   // not elevated: returned at the gate, before any prompt

        DialogService.Instance.Received(1).Confirm(
            Arg.Is<string>(m => m.Contains("Manual") && !m.Contains("set back to")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task EnableService_WithARememberedType_NamesThatTypeInThePrompt()
    {
        using var temp = new TempLedgerDir();
        var ledger = temp.NewLedger();
        ledger.Remember("Spooler", "Automatic", DateTimeOffset.UnixEpoch);

        var scanned = new List<ServiceEntry>
        {
            new() { Name = "Spooler", DisplayName = "Print Spooler", Status = "Stopped", StartType = "Disabled" },
        };
        using var vm = await CreateWithLedgerAsync(scanned, ledger);
        using var dialog = new DialogAnswer(confirm: false);

        await vm.EnableServiceCommand.ExecuteAsync(scanned[0]);

        if (dialog.Calls == 0) return;   // not elevated

        DialogService.Instance.Received(1).Confirm(
            Arg.Is<string>(m => m.Contains("set back to Automatic")),
            Arg.Any<string>());
    }

    [Fact]
    public void EveryMutatingServiceCommand_GoesThroughAConfirm()
    {
        // A source-level guard, because the runtime tests above cannot cover the elevated branch on a
        // non-elevated CI runner. Enable was missing its confirm precisely because three siblings had
        // one and nothing checked that the fourth did — so the count is asserted rather than assumed.
        var source = File.ReadAllText(Path.Combine(
            FindProjectDir(), "..", "SysManager", "ViewModels", "ServicesViewModel.cs"));

        var confirms = source.Split("DialogService.Instance.Confirm(").Length - 1;

        Assert.True(confirms >= 4,
            $"Expected a confirmation on all four mutating commands (Start, Stop, Disable, Enable) " +
            $"but found {confirms} calls to DialogService.Instance.Confirm.");
    }

    // ── Row marking ──────────────────────────────────────────────────────────────────────────────
    //
    // ToggleHighlightCommand and ServiceEntry.IsHighlighted shipped with the "row highlight" feature
    // commit, which touched two models, two view models and the CHANGELOG — and no view. So the
    // announced ability to "toggle highlight on any service row" had no button, and nothing rendered
    // IsHighlighted either. These tests cover the behaviour the UI now depends on; the binding itself
    // is asserted separately in ServicesViewMarkColumnTests, since a command the view does not bind is
    // exactly how this went unnoticed for so long.
    //
    // Each test seeds its OWN entries rather than the shared TestServices list: marking mutates the
    // entry, and mutating a static fixture would leak a mark into whichever test ran next.

    private static List<ServiceEntry> FreshEntries() =>
    [
        new() { Name = "wuauserv", DisplayName = "Windows Update", Description = "Manages Windows updates", Status = "Running", StartType = "Automatic", Recommendation = "keep-enabled", SafetyLevel = Models.SafetyLevel.Caution },
        new() { Name = "Spooler", DisplayName = "Print Spooler", Description = "Manages print jobs", Status = "Running", StartType = "Automatic", Recommendation = "safe-to-disable", SafetyLevel = Models.SafetyLevel.Caution },
        new() { Name = "XboxGipSvc", DisplayName = "Xbox Accessory Management", Description = "Manages Xbox accessories", Status = "Stopped", StartType = "Manual", Recommendation = "safe-to-disable", SafetyLevel = Models.SafetyLevel.Safe },
    ];

    [Fact]
    public async Task ToggleHighlight_MarksTheRow_AndCountsIt()
    {
        var entries = FreshEntries();
        var vm = await CreateWithDataAsync(entries);
        var target = entries[1];

        Assert.False(target.IsHighlighted);
        Assert.Equal(0, vm.HighlightedCount);

        vm.ToggleHighlightCommand.Execute(target);

        Assert.True(target.IsHighlighted);
        Assert.Equal(1, vm.HighlightedCount);
    }

    [Fact]
    public async Task ToggleHighlight_Twice_UnmarksTheRow()
    {
        var entries = FreshEntries();
        var vm = await CreateWithDataAsync(entries);
        var target = entries[0];

        vm.ToggleHighlightCommand.Execute(target);
        vm.ToggleHighlightCommand.Execute(target);

        Assert.False(target.IsHighlighted);
        Assert.Equal(0, vm.HighlightedCount);
    }

    [Fact]
    public async Task ToggleHighlight_IgnoresAnythingThatIsNotAServiceRow()
    {
        // The command takes object? because it is invoked with the row as CommandParameter. A stray
        // parameter must be a no-op, not a cast exception on the UI thread.
        var vm = await CreateWithDataAsync(FreshEntries());

        Assert.Null(Record.Exception(() => vm.ToggleHighlightCommand.Execute(null)));
        Assert.Null(Record.Exception(() => vm.ToggleHighlightCommand.Execute("not a service")));
        Assert.Equal(0, vm.HighlightedCount);
    }

    [Fact]
    public async Task AMarkedRow_SurvivesFilteringAndSearching()
    {
        // The point of the feature: mark a row, keep working, still find it. ApplyFilter re-projects
        // from the SAME _allServices instances, so the mark rides along — this test pins that, because
        // a future change to rebuild entries per filter pass would silently drop every mark.
        var entries = FreshEntries();
        var vm = await CreateWithDataAsync(entries);
        var spooler = entries[1];

        vm.ToggleHighlightCommand.Execute(spooler);

        vm.FilterText = "print";                       // narrow to the marked row
        Assert.Contains(vm.Services, s => ReferenceEquals(s, spooler) && s.IsHighlighted);

        vm.FilterText = "xbox";                        // filter it out entirely
        Assert.DoesNotContain(vm.Services, s => ReferenceEquals(s, spooler));
        Assert.True(spooler.IsHighlighted);            // still marked while hidden
        Assert.Equal(1, vm.HighlightedCount);          // and still counted

        vm.FilterText = "";                            // bring it back
        Assert.Contains(vm.Services, s => ReferenceEquals(s, spooler) && s.IsHighlighted);
    }

    [Fact]
    public async Task ClearHighlights_ClearsMarksOnRowsTheFilterIsHiding()
    {
        // The negative case that matters. Clearing from the VISIBLE collection would leave marks on
        // filtered-out rows, so "Clear 3 marks" would clear one and the button would stay on screen
        // claiming two more — a worse experience than no button at all. ClearHighlights walks
        // _allServices for exactly this reason.
        var entries = FreshEntries();
        var vm = await CreateWithDataAsync(entries);

        foreach (var e in entries) vm.ToggleHighlightCommand.Execute(e);
        Assert.Equal(3, vm.HighlightedCount);

        vm.FilterText = "print";                       // only one of the three is visible now
        Assert.Single(vm.Services);

        vm.ClearHighlightsCommand.Execute(null);

        Assert.All(entries, e => Assert.False(e.IsHighlighted));
        Assert.Equal(0, vm.HighlightedCount);
    }

    private static string FindProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SysManager.Tests.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the test project directory.");
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
