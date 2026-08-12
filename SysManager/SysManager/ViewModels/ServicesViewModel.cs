// SysManager · ServicesViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Services tab — lists all Windows services with gaming recommendations,
/// allows start/stop and startup type changes.
/// </summary>
public sealed partial class ServicesViewModel : ViewModelBase
{
    private readonly IPowerShellRunner _ps;
    private readonly ServiceStartupLedgerService _ledger;
    private List<ServiceEntry> _allServices = [];

    public BulkObservableCollection<ServiceEntry> Services { get; } = new();

    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _selectedFilter = "All";
    [ObservableProperty] private ServiceEntry? _selectedService;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _safeCount;
    [ObservableProperty] private int _cautionCount;
    [ObservableProperty] private int _criticalCount;

    // Counts for the filters that had no chip. Each chip shows its own count for the same reason the
    // safety chips do: "Safe to disable (12)" tells the user whether the filter is worth pressing before
    // they press it, and a count of 0 explains an empty list without them having to wonder.
    [ObservableProperty] private int _stoppedCount;
    [ObservableProperty] private int _safeToDisableCount;
    [ObservableProperty] private int _keepEnabledCount;
    [ObservableProperty] private int _advancedCount;

    /// <summary>
    /// How many rows the user has marked. Drives the visibility of the "Clear marks" button — with no
    /// marks there is nothing to clear, and a permanently visible dead button is the kind of control
    /// this fix exists to remove.
    /// </summary>
    [ObservableProperty] private int _highlightedCount;

    /// <summary>
    /// Every value <see cref="ApplyFilter"/> understands, in the order the chips appear.
    /// </summary>
    /// <remarks>
    /// <para>"Safe to disable" / "Keep enabled" / "Advanced" filter on the GAMING RECOMMENDATION, which
    /// is a different dataset from the Safe/Caution/Critical SAFETY level: safety answers "will this
    /// break Windows", the recommendation answers "is this worth turning off for games, and why".</para>
    /// <para>This array previously existed with nothing bound to it, and its comment claimed the
    /// README's "filter by recommendation level" promise had been made true — while five of the nine
    /// values (Running, Stopped, and all three recommendations) had no control at all, so they could
    /// only be reached from a debugger. The chips now cover all nine. The array is still not bound to a
    /// ComboBox: it is the single list the filter tests enumerate, so a value added here without a chip
    /// fails <c>EveryFilterOption_HasAChipInTheView</c> rather than going unnoticed again.</para>
    /// </remarks>
    public string[] FilterOptions { get; } =
        { "All", "Running", "Stopped", "Safe", "Caution", "Critical",
          "Safe to disable", "Keep enabled", "Advanced" };

    public ServicesViewModel(IPowerShellRunner ps, ServiceStartupLedgerService? ledger = null)
    {
        _ps = ps;
        _ledger = ledger ?? new ServiceStartupLedgerService();
        IsElevated = AdminHelper.IsElevated();
        InitializeAsync(InitAsync);
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    private async Task InitAsync()
    {
        try { await RefreshAsync(); }
        catch (InvalidOperationException ex) { Log.Warning("Services auto-refresh failed: {Error}", ex.Message); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warning("Services auto-refresh failed: {Error}", ex.Message); }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Loading services…";
        try
        {
            _allServices = await Task.Run(ServiceManagerService.GetAllServices);
            // GetAllServices builds fresh ServiceEntry objects, so anything Disable recorded on
            // the previous instances is gone. Re-attach it from the persisted ledger, or Enable
            // would restore an Automatic service as Manual after any refresh or restart.
            RehydratePreviousStartTypes();
            // Ensure collection updates happen on the UI thread to prevent
            // cross-thread exceptions when navigating during concurrent scans (#154).
            if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
                d.Invoke(ApplyFilterCore);
            else
                ApplyFilterCore();
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Service scan failed: {ex.Message}"; }
        catch (Win32Exception ex) { StatusMessage = $"Service scan failed: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    /// <summary>
    /// Copies each service's pre-disable startup type from the persisted ledger onto the freshly
    /// scanned entries. Only applied to services Windows currently reports as Disabled: if the user
    /// re-enabled one outside SysManager, a stale ledger entry must not override what the machine
    /// actually says.
    /// </summary>
    private void RehydratePreviousStartTypes()
    {
        var ledger = _ledger.Load();
        if (ledger.Count == 0) return;

        foreach (var entry in _allServices)
        {
            if (!string.Equals(entry.StartType, "Disabled", StringComparison.OrdinalIgnoreCase)) continue;
            if (ledger.TryGetValue(entry.Name, out var record))
                entry.PreviousStartType = record.PreviousStartType;
        }
    }

    private void ApplyFilterCore()
    {
        TotalCount = _allServices.Count;
        RunningCount = _allServices.Count(s => s.Status == "Running");
        // A refresh replaces every ServiceEntry, so the marks are gone with them — recount rather than
        // leaving a stale non-zero count that would keep offering to clear marks that no longer exist.
        UpdateHighlightCount();
        ApplyFilter();
        StatusMessage = $"Loaded {TotalCount} services ({RunningCount} running).";
        ToastService.Instance.Show("Services refreshed", $"{TotalCount} services ({RunningCount} running)");
    }

    [RelayCommand]
    private async Task StartServiceAsync(ServiceEntry? entry)
    {
        if (entry is null) return;
        if (!AdminHelper.IsElevated()) { StatusMessage = "⚠ Starting services requires admin."; return; }

        if (!DialogService.Instance.Confirm(
            $"Start service \"{entry.DisplayName}\"?",
            "Start Service — Confirm")) return;

        try
        {
            // Resume on the UI thread (no ConfigureAwait(false)): the continuation updates
            // bound state (RefreshStatus + StatusMessage), matching Enable/DisableServiceAsync.
            await ServiceManagerService.StartServiceAsync(entry.Name);
            ServiceManagerService.RefreshStatus(entry);
            StatusMessage = $"✓ {entry.DisplayName} started.";
            Log.Information("Service started: {ServiceName}", entry.Name);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Start service failed: {ex.Message}"; }
        catch (System.ServiceProcess.TimeoutException) { StatusMessage = $"Timeout starting {entry.DisplayName}."; }
    }

    [RelayCommand]
    private async Task StopServiceAsync(ServiceEntry? entry)
    {
        if (entry is null) return;

        // Stopping a boot/logon-critical service (RpcSs, DcomLaunch, ProfSvc, lsass, …)
        // can freeze the session or force a reboot just as surely as disabling it, so it
        // gets the same unconditional refusal as DisableServiceAsync rather than the
        // neutral "may affect system functionality" confirm. Checked before the elevation
        // guard — it can never proceed regardless of admin.
        if (entry.SafetyLevel == SafetyLevel.Critical)
        {
            StatusMessage = $"⛔ \"{entry.DisplayName}\" is critical and cannot be stopped — {entry.SafetyDescription}";
            Log.Warning("Refused to stop critical service: {ServiceName} ({DisplayName})", entry.Name, entry.DisplayName);
            return;
        }

        if (!AdminHelper.IsElevated()) { StatusMessage = "⚠ Stopping services requires admin."; return; }

        if (!DialogService.Instance.Confirm(
            $"Stop service \"{entry.DisplayName}\"?\n\nThis may affect system functionality.",
            "Stop Service — Confirm")) return;

        try
        {
            // Resume on the UI thread — see StartServiceAsync.
            await ServiceManagerService.StopServiceAsync(entry.Name);
            ServiceManagerService.RefreshStatus(entry);
            StatusMessage = $"✓ {entry.DisplayName} stopped.";
            Log.Information("Service stopped: {ServiceName}", entry.Name);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Stop service failed: {ex.Message}"; }
        catch (System.ServiceProcess.TimeoutException) { StatusMessage = $"Timeout stopping {entry.DisplayName}."; }
    }

    [RelayCommand]
    private async Task DisableServiceAsync(ServiceEntry? entry)
    {
        if (entry is null) return;

        // A boot/logon-critical service must never be disabled: setting RpcSs,
        // DcomLaunch, ProfSvc, lsass, etc. to Disabled can prevent Windows from
        // booting or logging in. Refuse outright rather than hide the risk behind
        // the same neutral confirm shown for safe-to-disable services. Checked
        // before the elevation guard — it can never proceed regardless of admin.
        if (entry.SafetyLevel == SafetyLevel.Critical)
        {
            StatusMessage = $"⛔ \"{entry.DisplayName}\" is critical and cannot be disabled — {entry.SafetyDescription}";
            Log.Warning("Refused to disable critical service: {ServiceName} ({DisplayName})", entry.Name, entry.DisplayName);
            return;
        }

        if (!AdminHelper.IsElevated()) { StatusMessage = "⚠ Changing startup type requires admin."; return; }

        if (!DialogService.Instance.Confirm(
            $"Disable service \"{entry.DisplayName}\"?\n\nThis prevents the service from starting automatically.",
            "Disable Service — Confirm")) return;

        // Snapshot the current startup type BEFORE disabling so Enable can restore the
        // exact previous type (e.g. Automatic) instead of always falling back to Manual.
        var previous = entry.StartType;

        try
        {
            await ServiceManagerService.SetStartupTypeAsync(entry.Name, "disabled", _ps);
            entry.PreviousStartType = previous;
            // Persist it too. The in-memory value is lost by the next scan, which rebuilds every
            // ServiceEntry — so without this, Enable after a refresh or restart silently restored
            // an Automatic service as Manual while reporting success.
            _ledger.Remember(entry.Name, previous, DateTimeOffset.UtcNow);
            ServiceManagerService.RefreshStatus(entry);
            StatusMessage = $"✓ {entry.DisplayName} set to Disabled.";
            Log.Information("Service disabled: {ServiceName} (was {Previous})", entry.Name, previous);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Disable service failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task EnableServiceAsync(ServiceEntry? entry)
    {
        if (entry is null) return;
        if (!AdminHelper.IsElevated()) { StatusMessage = "⚠ Changing startup type requires admin."; return; }

        // Restore the startup type the service had before SysManager disabled it. Prefer the
        // persisted ledger over the in-memory value: the property is wiped by every scan, so
        // in-memory only survives until the next Refresh. If neither knows, fall back to Manual
        // (the conservative default that StartTypeToScToken applies to an unknown value).
        var previous = _ledger.PreviousStartTypeFor(entry.Name) ?? entry.PreviousStartType;
        var targetToken = ServiceManagerService.StartTypeToScToken(previous);

        // Confirm, like Start / Stop / Disable already do. This is a persistent, machine-scope change
        // to a Windows service, and it was the ONE mutating command on this tab with no prompt —
        // reachable in a single click, because the Enable button renders on every row with no
        // Visibility or CanExecute guard.
        //
        // The wording branches on whether the original type is known, because when it is not this
        // command does NOT restore anything: `previous` is null and StartTypeToScToken's `_ =>
        // "demand"` fallback sets the service to Manual. That is the likely case for a service the
        // user disabled outside SysManager, so the prompt must say "set to Manual" rather than
        // "restored" — otherwise it describes an action the app is not performing.
        var message = string.IsNullOrWhiteSpace(previous)
            ? $"Enable service \"{entry.DisplayName}\"?\n\n" +
              "SysManager has no record of how this service was set before, so it will be set to " +
              "Manual — Windows starts it when something needs it, rather than at every boot. If it " +
              "used to start automatically, you can change that yourself afterwards."
            : $"Enable service \"{entry.DisplayName}\"?\n\n" +
              $"Its startup type will be set back to {previous}, which is what it was before " +
              "SysManager disabled it.";

        if (!DialogService.Instance.Confirm(message, "Enable Service — Confirm")) return;

        try
        {
            await ServiceManagerService.SetStartupTypeAsync(entry.Name, targetToken, _ps);
            entry.PreviousStartType = null;
            _ledger.Forget(entry.Name);
            ServiceManagerService.RefreshStatus(entry);
            StatusMessage = $"✓ {entry.DisplayName} set to {entry.StartType}.";
            Log.Information("Service enabled: {ServiceName} -> {StartType}", entry.Name, entry.StartType);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Enable service failed: {ex.Message}"; }
    }

    private void ApplyFilter()
    {
        var filtered = _allServices.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(FilterText))
            filtered = filtered.Where(s =>
                s.DisplayName.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

        filtered = SelectedFilter switch
        {
            "Running" => filtered.Where(s => s.Status == "Running"),
            "Stopped" => filtered.Where(s => s.Status == "Stopped"),
            "Safe" => filtered.Where(s => s.SafetyLevel == SafetyLevel.Safe),
            "Caution" => filtered.Where(s => s.SafetyLevel == SafetyLevel.Caution),
            "Critical" => filtered.Where(s => s.SafetyLevel == SafetyLevel.Critical),
            // Gaming recommendation, not safety level — see FilterOptions. The stored values are the
            // literals from ServiceManagerService.GamingGuide, which uses exactly three:
            // safe-to-disable (12 entries), keep-enabled (9) and advanced (4).
            "Safe to disable" => filtered.Where(s => s.Recommendation == "safe-to-disable"),
            "Keep enabled" => filtered.Where(s => s.Recommendation == "keep-enabled"),
            "Advanced" => filtered.Where(s => s.Recommendation == "advanced"),
            _ => filtered
        };

        Services.ReplaceWith(filtered.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase));

        // One pass for every chip's count. Counted over _allServices rather than the filtered result, so
        // each chip shows how many it WOULD match — a count that shrank to reflect the active filter
        // would make the other chips look empty and unpressable.
        int safe = 0, caution = 0, critical = 0;
        int stopped = 0, safeToDisable = 0, keepEnabled = 0, advanced = 0;
        foreach (var s in _allServices)
        {
            switch (s.SafetyLevel)
            {
                case SafetyLevel.Safe: safe++; break;
                case SafetyLevel.Caution: caution++; break;
                case SafetyLevel.Critical: critical++; break;
            }

            // "Stopped" counted explicitly rather than as Total - Running: Windows also reports
            // StartPending / StopPending / Paused, so the two are not complements and subtracting would
            // over-count whenever a service is mid-transition.
            if (s.Status == "Stopped") stopped++;

            switch (s.Recommendation)
            {
                case "safe-to-disable": safeToDisable++; break;
                case "keep-enabled": keepEnabled++; break;
                case "advanced": advanced++; break;
            }
        }
        SafeCount = safe;
        CautionCount = caution;
        CriticalCount = critical;
        StoppedCount = stopped;
        SafeToDisableCount = safeToDisable;
        KeepEnabledCount = keepEnabled;
        AdvancedCount = advanced;
    }

    /// <summary>
    /// Marks or unmarks one service row, so a user working through a long list can keep track of the
    /// entries they care about. Bound from the grid's mark column.
    /// </summary>
    /// <remarks>
    /// The mark lives on the <see cref="ServiceEntry"/> instance, and <see cref="ApplyFilter"/> filters
    /// and sorts the SAME instances out of <c>_allServices</c> rather than projecting new ones, so a
    /// mark survives searching, filter chips and column sorting. A Refresh re-queries Windows and
    /// therefore builds new entries, which clears the marks — correct, since the rows are no longer the
    /// same observations.
    /// </remarks>
    [RelayCommand]
    private void ToggleHighlight(object? parameter)
    {
        if (parameter is not ServiceEntry entry) return;
        entry.IsHighlighted = !entry.IsHighlighted;
        UpdateHighlightCount();
    }

    /// <summary>Clears every mark, so the user is never stuck hunting marked rows one by one.</summary>
    [RelayCommand]
    private void ClearHighlights()
    {
        // _allServices, not Services: a mark can be on a row the current filter hides, and "Clear
        // marks" that left invisible marks behind would be the same broken promise as the feature
        // having no button at all.
        foreach (var entry in _allServices)
            entry.IsHighlighted = false;
        UpdateHighlightCount();
    }

    private void UpdateHighlightCount() => HighlightedCount = _allServices.Count(s => s.IsHighlighted);
}
