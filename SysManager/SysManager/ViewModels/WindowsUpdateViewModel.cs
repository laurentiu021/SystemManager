// SysManager · WindowsUpdateViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

public sealed partial class WindowsUpdateViewModel : ViewModelBase
{
    internal const string PsWindowsUpdateInstallScript = """
        $ErrorActionPreference = 'Stop'
        Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
        $gallery = Get-PSRepository -Name PSGallery -ErrorAction Stop
        $gallerySource = ([string]$gallery.SourceLocation).TrimEnd('/')
        if ($gallerySource -ne 'https://www.powershellgallery.com/api/v2') {
            throw "PSGallery points to an unexpected source: $gallerySource"
        }
        if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
            Install-PackageProvider -Name NuGet -Force -Scope CurrentUser | Out-Null
        }
        Install-Module -Name PSWindowsUpdate -Force -Scope CurrentUser -Repository PSGallery -AllowClobber
        Import-Module PSWindowsUpdate
        'INSTALLED'
        """;

    internal const int HistoryModuleImportFailedExitCode = 41;
    internal const int HistoryQueryFailedExitCode = 42;

    internal const string PsWindowsUpdateHistoryScript = """
        $ErrorActionPreference = 'Stop'
        try {
            Import-Module PSWindowsUpdate -ErrorAction Stop
        }
        catch {
            [Console]::Error.WriteLine("PSWindowsUpdate import failed: $($_.Exception.Message)")
            exit 41
        }

        try {
            $hist = Get-WUHistory -Last 30 -ErrorAction Stop
        }
        catch {
            [Console]::Error.WriteLine("PSWindowsUpdate history query failed: $($_.Exception.Message)")
            exit 42
        }

        if (-not $hist -or $hist.Count -eq 0) { '[]' }
        else {
            $hist | Select-Object @{N='Title';E={$_.Title}},
                @{N='KB';E={if($_.KBArticleIDs){('KB'+($_.KBArticleIDs -join ','))}else{''}}},
                @{N='Size';E={''}},
                @{N='Status';E={$_.Result}},
                @{N='Date';E={if($_.Date){$_.Date.ToString('yyyy-MM-dd')}else{''}}},
                @{N='IsHidden';E={$false}},
                @{N='Category';E={'History'}} |
            ConvertTo-Json -Compress
        }
        """;

    private readonly IPowerShellRunner _runner;
    private readonly WindowsUpdateService _wu;
    private readonly WindowsUpdatePolicyService _policy;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<UpdateEntry> Updates { get; } = new();
    public ConsoleViewModel Console { get; } = new();

    [ObservableProperty] private bool _moduleAvailable;
    [ObservableProperty] private string _moduleStatus = "Checking PSWindowsUpdate module...";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private int _updateCount;
    [ObservableProperty] private string _tableSummary = "";
    [ObservableProperty] private bool _showConsole;
    [ObservableProperty] private bool _isShowingHistory;

    /// <summary>
    /// Backs the grid's select-all header checkbox. Setting it selects or
    /// deselects every row; a guard prevents re-entrancy with row toggles.
    /// </summary>
    [ObservableProperty] private bool _allSelected;
    private bool _suppressAllSelected;

    // Update deferral policy (registry-backed, reversible).
    [ObservableProperty] private string _policySummary = "";
    [ObservableProperty] private int _deferDays = 30;
    [ObservableProperty] private int _pauseDays = 7;

    public WindowsUpdateViewModel(
        IPowerShellRunner runner,
        WindowsUpdateService wu,
        WindowsUpdatePolicyService policy)
        : this(runner, wu, policy, AdminHelper.IsElevated)
    {
    }

    internal WindowsUpdateViewModel(
        IPowerShellRunner runner,
        WindowsUpdateService wu,
        WindowsUpdatePolicyService policy,
        Func<bool> isElevated)
    {
        _runner = runner;
        _wu = wu;
        _policy = policy;
        _runner.LineReceived += OnRunnerLineReceived;
        _runner.ProgressChanged += OnRunnerProgressChanged;
        _wu.Log += OnWuLog;
        // IsBusy lives in the base class, so observe it here to re-evaluate the
        // long-running commands' CanExecute (disabling them while one runs prevents
        // a second command disposing the shared CTS the first is still awaiting).
        PropertyChanged += OnVmPropertyChanged;
        IsElevated = (isElevated ?? throw new ArgumentNullException(nameof(isElevated)))();
        // PSWindowsUpdate is only needed for the History view, so we don't
        // probe for it at startup — the History command checks itself if
        // the module is missing. This keeps the constructor side-effect-free
        // and IsBusy stays false until the user triggers an action.
        ModuleAvailable = true;
        ModuleStatus = "PSWindowsUpdate is used for the History view only.";
        RefreshPolicy();
    }

    private void RefreshPolicy() => PolicySummary = _policy.Read(DateTime.Now).Summary;

    [RelayCommand]
    private void DeferFeatureUpdates()
    {
        if (!RequireAdminForPolicy()) return;
        if (!DialogService.Instance.Confirm(
                $"Defer Windows feature updates by {WindowsUpdatePolicyService.ClampDeferDays(DeferDays)} days?\n\n" +
                "Security and quality updates keep installing normally — only large feature upgrades are held back. " +
                "Reversible any time with \"Restore default\".",
                "Defer feature updates"))
            return;
        PolicySummary = _policy.DeferFeatureUpdates(DeferDays)
            ? _policy.Read(DateTime.Now).Summary
            : "Couldn't apply the policy — administrator rights are required.";
    }

    [RelayCommand]
    private void PauseUpdates()
    {
        if (!RequireAdminForPolicy()) return;
        var clamped = WindowsUpdatePolicyService.ClampPauseDays(PauseDays);
        if (!DialogService.Instance.Confirm(
                $"Pause all Windows updates for {clamped} days?\n\n" +
                "Windows will automatically resume updates when the pause ends. This is a bounded pause — " +
                "SysManager never disables updates permanently.",
                "Pause updates"))
            return;
        PolicySummary = _policy.PauseUpdates(PauseDays, DateTime.Now)
            ? _policy.Read(DateTime.Now).Summary
            : "Couldn't apply the policy — administrator rights are required.";
    }

    [RelayCommand]
    private void RestoreUpdatePolicy()
    {
        if (!RequireAdminForPolicy()) return;
        // Confirmed like its two siblings above. This is the one button on the panel that DISCARDS
        // configuration — it clears whatever deferral and pause window the user set — and it was the
        // only one of the three that did not ask. The summary line describes the state being thrown
        // away, so the user can see what they are giving up before agreeing to it.
        if (!DialogService.Instance.Confirm(
                "Restore Windows Update to its default settings?\n\n" +
                $"This clears any deferral or pause you have configured. Current state: {PolicySummary}",
                "Restore update defaults"))
            return;
        PolicySummary = _policy.RestoreDefault()
            ? _policy.Read(DateTime.Now).Summary
            : "Couldn't restore the policy — administrator rights are required.";
    }

    private bool RequireAdminForPolicy()
    {
        if (IsElevated) return true;
        PolicySummary = "Changing update policy requires administrator rights — use \"Run as administrator\".";
        return false;
    }

    private void OnRunnerLineReceived(PowerShellLine l) => Console.Append(l);

    /// <summary>
    /// A real percentage arrived from the PowerShell progress stream, so switch the bar out of
    /// indeterminate mode — WPF ignores <c>Value</c> entirely while <c>IsIndeterminate</c> is true, so
    /// assigning <see cref="ViewModelBase.Progress"/> alone would keep the bar sweeping and never fill
    /// it. Every command sets the flag back to true on entry and clears it in its own finally, so this
    /// only ever narrows the indeterminate window to "before the first percentage". Mirrors
    /// CleanupViewModel, which pairs the two assignments the same way.
    /// </summary>
    private void OnRunnerProgressChanged(int p)
    {
        Progress = p;
        IsProgressIndeterminate = false;
    }

    private void OnWuLog(string text) => Console.Append(PowerShellLine.Output(text));

    /// <summary>
    /// Gate for the long-running commands. They all share <see cref="_cts"/> and each
    /// recreates it; without a re-entrancy guard a second command could dispose the CTS
    /// the first is still awaiting (ObjectDisposedException). Disabling the buttons
    /// while one runs makes that impossible. <see cref="ViewModelBase.IsBusy"/> lives in
    /// the base class, so each command notifies the gate manually around its run
    /// (mirroring WindowsFeaturesViewModel) rather than via an OnIsBusyChanged hook.
    /// </summary>
    private bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        ListUpdatesCommand.NotifyCanExecuteChanged();
        ShowHistoryCommand.NotifyCanExecuteChanged();
        CheckPendingRebootCommand.NotifyCanExecuteChanged();
        InstallUpdatesCommand.NotifyCanExecuteChanged();
        CheckModuleCommand.NotifyCanExecuteChanged();
        InstallModuleCommand.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runner.LineReceived -= OnRunnerLineReceived;
            _runner.ProgressChanged -= OnRunnerProgressChanged;
            _wu.Log -= OnWuLog;
            PropertyChanged -= OnVmPropertyChanged;
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    // Gated on NotBusy like the other runner-driven commands: CheckModule and
    // InstallModule stream through the shared _runner into the same console, so letting
    // them start while another update operation is running would cross-contaminate output
    // and race on the shared cancellation state.
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CheckModuleAsync()
    {
        IsBusy = true;
        try
        {
            ModuleAvailable = await ProbeModuleAvailabilityAsync();
            ModuleStatus = ModuleAvailable
                ? "PSWindowsUpdate is available (used for update history)."
                : MissingModuleStatus();
        }
        catch (InvalidOperationException ex) { ModuleStatus = $"Check failed: {ex.Message}"; }
        catch (OperationCanceledException) { ModuleStatus = "Module check cancelled."; }
        finally { IsBusy = false; }
    }

    private async Task<bool> ProbeModuleAvailabilityAsync()
    {
        var found = false;
        void Listen(PowerShellLine line)
        {
            if (line.Kind == OutputKind.Output &&
                line.Text.Contains("AVAILABLE", StringComparison.Ordinal))
            {
                found = true;
            }
        }

        _runner.LineReceived += Listen;
        try
        {
            var exitCode = await _runner.RunScriptViaPwshAsync(
                "if (Get-Module -ListAvailable -Name PSWindowsUpdate) { 'AVAILABLE' } else { 'MISSING' }");
            return exitCode == 0 && found;
        }
        finally
        {
            _runner.LineReceived -= Listen;
        }
    }

    private string MissingModuleStatus() => IsElevated
        ? "Update history is unavailable in administrator mode. Reopen SysManager normally to install or use the current-user module."
        : "PSWindowsUpdate is not installed. Install it to enable update history.";

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task InstallModuleAsync()
    {
        if (IsElevated)
        {
            StatusMessage = "Install PSWindowsUpdate from a normal, non-administrator SysManager session.";
            return;
        }
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Installing PSWindowsUpdate...";
        ShowConsole = true;
        try
        {
            var exitCode = await _runner.RunScriptViaPwshAsync(PsWindowsUpdateInstallScript);
            if (exitCode != 0)
            {
                ModuleAvailable = false;
                ModuleStatus = "PSWindowsUpdate installation failed. Review the console output.";
                StatusMessage = ModuleStatus;
                return;
            }

            ModuleAvailable = await ProbeModuleAvailabilityAsync();
            ModuleStatus = ModuleAvailable
                ? "PSWindowsUpdate is available (used for update history)."
                : "Installation completed, but PSWindowsUpdate could not be loaded.";
            StatusMessage = ModuleStatus;
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (OperationCanceledException) { StatusMessage = "Module install cancelled."; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ListUpdatesAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        IsShowingHistory = false;
        StatusMessage = "Scanning for available Windows Updates…";
        Updates.Clear();
        ShowConsole = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var results = await _wu.ScanAsync(_cts.Token);
            foreach (var u in results.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase))
                Updates.Add(u);

            UpdateCount = Updates.Count;
            // Newly listed updates default to selected, so reflect that on the
            // header checkbox without re-applying to rows.
            _suppressAllSelected = true;
            AllSelected = UpdateCount > 0;
            _suppressAllSelected = false;
            TableSummary = UpdateCount > 0
                ? $"{UpdateCount} updates found."
                : "No updates available.";
            StatusMessage = "Scan complete";
            ToastService.Instance.Show("Windows Update scan complete", $"{UpdateCount} updates found");
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            StatusMessage = $"Windows Update Agent error: 0x{ex.HResult:X8}";
            Log.Warning(ex, "WUA scan failed");
        }
        catch (UnauthorizedAccessException)
        {
            StatusMessage = "Access denied — run SysManager as administrator.";
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ShowHistoryAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        IsShowingHistory = true;
        StatusMessage = "Loading update history…";
        Updates.Clear();
        UpdateCount = 0;
        TableSummary = "";
        AllSelected = false;
        ShowConsole = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var json = new System.Text.StringBuilder();
            void Capture(PowerShellLine l)
            {
                if (l.Kind == OutputKind.Output)
                    json.AppendLine(l.Text);
            }

            _runner.LineReceived += Capture;
            try
            {
                var exitCode = await _runner.RunScriptViaPwshAsync(
                    PsWindowsUpdateHistoryScript,
                    cancellationToken: _cts.Token);
                if (exitCode != 0)
                {
                    SetHistoryFailureState(exitCode);
                    return;
                }
            }
            finally { _runner.LineReceived -= Capture; }

            ModuleAvailable = true;
            ModuleStatus = "PSWindowsUpdate is available (used for update history).";
            if (!ParseUpdateJson(json.ToString()))
            {
                TableSummary = "Update history unavailable.";
                ShowConsole = true;
                StatusMessage = "Update history returned invalid data.";
                return;
            }
            UpdateCount = Updates.Count;
            TableSummary = $"{UpdateCount} history entries.";
            StatusMessage = "Done";
            ToastService.Instance.Show("Update History complete", $"{UpdateCount} history entries");
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        catch (InvalidOperationException ex)
        {
            TableSummary = "Update history unavailable.";
            ModuleAvailable = false;
            ModuleStatus = "PSWindowsUpdate availability could not be confirmed.";
            ShowConsole = true;
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    private void SetHistoryFailureState(int exitCode)
    {
        TableSummary = "Update history unavailable.";
        ShowConsole = true;
        switch (exitCode)
        {
            case HistoryModuleImportFailedExitCode:
                ModuleAvailable = false;
                ModuleStatus = MissingModuleStatus();
                StatusMessage = ModuleStatus;
                break;
            case HistoryQueryFailedExitCode:
                ModuleAvailable = true;
                ModuleStatus = "PSWindowsUpdate is available (used for update history).";
                StatusMessage = "Update history query failed. Review the console output.";
                break;
            default:
                ModuleAvailable = false;
                ModuleStatus = "PSWindowsUpdate availability could not be confirmed.";
                StatusMessage = "Update history could not be loaded. Review the console output.";
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CheckPendingRebootAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Checking pending reboot…";
        ShowConsole = true;
        Console.ClearCommand.Execute(null);
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                $pending = $false
                $reasons = @()
                if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') {
                    $pending = $true; $reasons += 'Component Based Servicing'
                }
                if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') {
                    $pending = $true; $reasons += 'Windows Update'
                }
                try {
                    $p = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction Stop).PendingFileRenameOperations
                    if ($p) { $pending = $true; $reasons += 'Pending file rename operations' }
                } catch {}
                if ($pending) { ""REBOOT REQUIRED - reasons: $($reasons -join ', ')"" }
                else         { 'No pending reboot detected.' }
            ", cancellationToken: _cts.Token);
            StatusMessage = "Done";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task InstallUpdatesAsync()
    {
        var selected = Updates.Where(u => u.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No updates selected.";
            return;
        }

        if (!DialogService.Instance.Confirm(
                $"Install {selected.Count} selected Windows update(s)?\n\n" +
                "This may install drivers or feature updates and can require a restart. " +
                "Do not reboot while the install is in progress.",
                "Confirm Windows Update"))
            return;

        if (!AdminHelper.IsElevated())
        {
            StatusMessage = "Admin required. Relaunching elevated...";
            if (AdminHelper.RelaunchAsAdmin()) System.Windows.Application.Current?.Shutdown();
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = $"Installing {selected.Count} update(s) (do not reboot)…";
        ShowConsole = true;
        Console.ClearCommand.Execute(null);
        foreach (var u in selected) u.Status = "Pending…";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var report = await _wu.InstallAsync(selected, _cts.Token);
            var notApplied = selected.Count - report.Installed - report.Failed;
            StatusMessage = report.Failed > 0 || notApplied > 0
                ? $"Installed {report.Installed}/{selected.Count}. Failed: {report.Failed}. Not applied: {notApplied}."
                : $"Installed {report.Installed}/{selected.Count}.";

            if (report.Installed > 0)
                ToastService.Instance.Show("Windows Update", report.RebootRequired
                    ? $"Installed {report.Installed} — reboot required"
                    : $"Installed {report.Installed} update(s)");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
            foreach (var u in selected.Where(s => s.Status is "Pending…" or "Downloading…" or "Installing…"))
                u.Status = "Cancelled";
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            StatusMessage = $"WUA error: 0x{ex.HResult:X8}";
            Log.Warning(ex, "WUA install failed");
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private void SelectAll() => SetAllSelected(true);

    [RelayCommand]
    private void DeselectAll() => SetAllSelected(false);

    /// <summary>
    /// Applies a selection state to every row and reflects it on the header
    /// checkbox (<see cref="AllSelected"/>). Used by both the toolbar buttons
    /// and the header checkbox toggle. The <c>_suppressAllSelected</c> guard
    /// prevents the header sync from re-entering the change handler, and the
    /// row loop always runs (even when <see cref="AllSelected"/> doesn't change
    /// value), so a header toggle is never a no-op against pre-set rows.
    /// </summary>
    private void SetAllSelected(bool value)
    {
        foreach (var u in Updates) u.IsSelected = value;
        if (AllSelected != value)
        {
            _suppressAllSelected = true;
            AllSelected = value;
            _suppressAllSelected = false;
        }
    }

    partial void OnAllSelectedChanged(bool value)
    {
        // Ignore programmatic syncs from SetAllSelected; only react to the
        // header checkbox being toggled directly by the user.
        if (_suppressAllSelected) return;
        SetAllSelected(value);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private bool ParseUpdateJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : [root];

            foreach (var entry in items.Select(el => new UpdateEntry
            {
                Title = el.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "",
                KB = el.TryGetProperty("KB", out var kb) ? kb.GetString() ?? "" : "",
                Size = el.TryGetProperty("Size", out var sz) ? FormatSize(sz) : "",
                Status = el.TryGetProperty("Status", out var st) ? st.GetString() ?? "" : "",
                Date = ParseDate(el.TryGetProperty("Date", out var dt) ? dt : default),
                IsHidden = el.TryGetProperty("IsHidden", out var ih) && ih.ValueKind == JsonValueKind.True,
                Category = el.TryGetProperty("Category", out var cat) ? cat.GetString() ?? "" : "",
            }))
            {
                Updates.Add(entry);
            }
            return true;
        }
        catch (JsonException ex)
        {
            Log.Warning("Failed to parse update JSON: {Error}", ex.Message);
            return false;
        }
    }

    private static string FormatSize(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number)
        {
            var bytes = el.GetInt64();
            return bytes switch
            {
                >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
                >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
                >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
                _ => $"{bytes} B"
            };
        }
        return el.GetString() ?? "";
    }

    private static DateTime? ParseDate(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return null;

        var text = el.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;

        return null;
    }
}
