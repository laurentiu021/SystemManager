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
    private readonly PowerShellRunner _runner;
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

    public WindowsUpdateViewModel(PowerShellRunner runner)
    {
        _runner = runner;
        _runner.LineReceived += OnRunnerLineReceived;
        _runner.ProgressChanged += OnRunnerProgressChanged;
        IsElevated = AdminHelper.IsElevated();
        InitializeAsync(InitAsync);
    }

    private void OnRunnerLineReceived(PowerShellLine l) => Console.Append(l);
    private void OnRunnerProgressChanged(int p) => Progress = p;

    private async Task InitAsync()
    {
        try { await AutoCheckOnStartAsync(); }
        catch (InvalidOperationException ex) { Log.Warning("Windows Update auto-check failed: {Error}", ex.Message); }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runner.LineReceived -= OnRunnerLineReceived;
            _runner.ProgressChanged -= OnRunnerProgressChanged;
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task AutoCheckOnStartAsync()
    {
        try
        {
            await Task.Delay(1);
            await CheckModuleAsync();
        }
        catch (OperationCanceledException) { /* expected on shutdown — no action needed */ }
        catch (InvalidOperationException ex) { Log.Warning("Module check failed: {Error}", ex.Message); }
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand]
    private async Task CheckModuleAsync()
    {
        IsBusy = true;
        try
        {
            var found = false;
            void Listen(PowerShellLine l)
            {
                if (l.Kind == OutputKind.Output && l.Text.Contains("AVAILABLE")) found = true;
            }
            _runner.LineReceived += Listen;
            try
            {
                await _runner.RunScriptViaPwshAsync(
                    "if (Get-Module -ListAvailable -Name PSWindowsUpdate) { 'AVAILABLE' } else { 'MISSING' }");
            }
            finally { _runner.LineReceived -= Listen; }
            ModuleAvailable = found;
            ModuleStatus = ModuleAvailable
                ? "PSWindowsUpdate is available."
                : "PSWindowsUpdate not installed — click Install Module.";
        }
        catch (InvalidOperationException ex) { ModuleStatus = $"Check failed: {ex.Message}"; }
        catch (OperationCanceledException) { ModuleStatus = "Module check cancelled."; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallModuleAsync()
    {
        if (!AdminHelper.IsElevated())
        {
            StatusMessage = "Requesting admin rights...";
            if (AdminHelper.RelaunchAsAdmin()) System.Windows.Application.Current?.Shutdown();
            return;
        }
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Installing PSWindowsUpdate...";
        ShowConsole = true;
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
                if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
                    Install-PackageProvider -Name NuGet -Force -Scope CurrentUser | Out-Null
                }
                Install-Module -Name PSWindowsUpdate -Force -Scope CurrentUser -AllowClobber
                Import-Module PSWindowsUpdate
                'INSTALLED'
            ");
            await CheckModuleAsync();
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        catch (OperationCanceledException) { StatusMessage = "Module install cancelled."; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private async Task ListUpdatesAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        IsShowingHistory = false;
        StatusMessage = "Listing available Windows Updates…";
        Updates.Clear();
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
                await _runner.RunScriptViaPwshAsync(@"
                    Import-Module PSWindowsUpdate -ErrorAction Stop
                    $updates = @()

                    $catExpr = {
                        if     ($_.Title -match 'Defender|Definition Update|Antimalware') { 'Defender' }
                        elseif ($_.Title -match 'Driver')                                 { 'Driver' }
                        elseif ($_.Title -match 'Cumulative Update')                      { 'Cumulative' }
                        elseif ($_.Title -match 'Security Update')                        { 'Security' }
                        elseif ($_.Title -match 'Servicing Stack')                        { 'Servicing' }
                        elseif ($_.Title -match '\.NET')                                  { '.NET' }
                        else                                                              { 'Update' }
                    }

                    $std = Get-WindowsUpdate -MicrosoftUpdate -ErrorAction SilentlyContinue
                    if ($std) {
                        $updates += $std | Select-Object @{N='Title';E={$_.Title}},
                            @{N='KB';E={if($_.KBArticleIDs){('KB'+($_.KBArticleIDs -join ','))}else{''}}},
                            @{N='Size';E={$_.Size}},
                            @{N='Status';E={'Available'}},
                            @{N='Date';E={$null}},
                            @{N='IsHidden';E={$false}},
                            @{N='Category';E=$catExpr}
                    }

                    try {
                        $up = Get-WindowsUpdate -MicrosoftUpdate -UpdateType Software -Category 'Upgrades' -ErrorAction SilentlyContinue
                        if ($up) {
                            $updates += $up | Select-Object @{N='Title';E={$_.Title}},
                                @{N='KB';E={if($_.KBArticleIDs){('KB'+($_.KBArticleIDs -join ','))}else{''}}},
                                @{N='Size';E={$_.Size}},
                                @{N='Status';E={'Available'}},
                                @{N='Date';E={$null}},
                                @{N='IsHidden';E={$false}},
                                @{N='Category';E={'Feature upgrade'}}
                        }
                    } catch {}

                    try {
                        $hidden = Get-WindowsUpdate -MicrosoftUpdate -IsHidden -ErrorAction SilentlyContinue
                        if ($hidden) {
                            $updates += $hidden | Select-Object @{N='Title';E={$_.Title}},
                                @{N='KB';E={if($_.KBArticleIDs){('KB'+($_.KBArticleIDs -join ','))}else{''}}},
                                @{N='Size';E={$_.Size}},
                                @{N='Status';E={'Hidden'}},
                                @{N='Date';E={$null}},
                                @{N='IsHidden';E={$true}},
                                @{N='Category';E={'Hidden'}}
                        }
                    } catch {}

                    $updates = $updates | Sort-Object Title -Unique
                    if (-not $updates -or @($updates).Count -eq 0) { '[]' }
                    else { @($updates) | ConvertTo-Json -Compress -Depth 3 }
                ", cancellationToken: _cts.Token);
            }
            finally { _runner.LineReceived -= Capture; }

            ParseUpdateJson(json.ToString());
            UpdateCount = Updates.Count;
            TableSummary = UpdateCount > 0
                ? $"{UpdateCount} updates found."
                : "No updates available.";
            StatusMessage = "Scan complete";
            ToastService.Instance.Show("Windows Update scan complete", $"{UpdateCount} updates found");
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private async Task ShowHistoryAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        IsShowingHistory = true;
        StatusMessage = "Loading update history…";
        Updates.Clear();
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
                await _runner.RunScriptViaPwshAsync(@"
                    Import-Module PSWindowsUpdate -ErrorAction Stop
                    $hist = Get-WUHistory -Last 30 -ErrorAction SilentlyContinue
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
                ", cancellationToken: _cts.Token);
            }
            finally { _runner.LineReceived -= Capture; }

            ParseUpdateJson(json.ToString());
            UpdateCount = Updates.Count;
            TableSummary = $"{UpdateCount} history entries.";
            StatusMessage = "Done";
            ToastService.Instance.Show("Update History complete", $"{UpdateCount} history entries");
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
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

    [RelayCommand]
    private async Task InstallUpdatesAsync()
    {
        var selected = Updates.Where(u => u.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No updates selected.";
            return;
        }

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
        foreach (var u in selected) u.Status = "Installing…";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        var resultJson = new System.Text.StringBuilder();
        void Capture(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output && l.Text.StartsWith("__RESULT__:", StringComparison.Ordinal))
                resultJson.AppendLine(l.Text.Substring("__RESULT__:".Length));
        }

        try
        {
            // PowerShell single-quoted strings escape ' as ''. Build a literal array of titles.
            var titleArray = string.Join(",", selected.Select(u => "'" + u.Title.Replace("'", "''") + "'"));

            _runner.LineReceived += Capture;
            try
            {
                await _runner.RunScriptViaPwshAsync($@"
                    Import-Module PSWindowsUpdate -ErrorAction Stop
                    $titles = @({titleArray})
                    $matched = Get-WindowsUpdate -MicrosoftUpdate -ErrorAction SilentlyContinue |
                               Where-Object {{ $titles -contains $_.Title }}
                    if (-not $matched -or @($matched).Count -eq 0) {{
                        $up = Get-WindowsUpdate -MicrosoftUpdate -UpdateType Software -Category 'Upgrades' -ErrorAction SilentlyContinue |
                              Where-Object {{ $titles -contains $_.Title }}
                        if ($up) {{ $matched = $up }}
                    }}
                    if (-not $matched -or @($matched).Count -eq 0) {{
                        Write-Host 'No matching updates found in the live update feed. They may already be installed or no longer offered.'
                        '__RESULT__:[]'
                        return
                    }}
                    $installed = $matched | Install-WindowsUpdate -AcceptAll -IgnoreReboot -Verbose
                    $report = $installed | Select-Object @{{N='Title';E={{$_.Title}}}},
                        @{{N='Result';E={{
                            if ($_.Result) {{ $_.Result.ToString() }}
                            elseif ($_.Status) {{ $_.Status.ToString() }}
                            else {{ 'Unknown' }}
                        }}}}
                    if (-not $report) {{ '__RESULT__:[]' }}
                    else {{ '__RESULT__:' + (@($report) | ConvertTo-Json -Compress -Depth 3) }}
                ", cancellationToken: _cts.Token);
            }
            finally { _runner.LineReceived -= Capture; }

            var (installed, failed, results) = ParseInstallResults(resultJson.ToString());
            ApplyInstallResults(selected, results);

            var notInstalled = selected.Count - installed - failed;
            StatusMessage = failed > 0 || notInstalled > 0
                ? $"Installed {installed}/{selected.Count}. Failed: {failed}. Not applied: {notInstalled}."
                : $"Installed {installed}/{selected.Count}.";

            if (installed > 0)
                ToastService.Instance.Show("Windows Update", $"Installed {installed} update(s)");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
            foreach (var u in selected.Where(s => s.Status == "Installing…")) u.Status = "Cancelled";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            foreach (var u in selected.Where(s => s.Status == "Installing…")) u.Status = "Error";
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    /// <summary>
    /// Parse the JSON report emitted by Install-WindowsUpdate
    /// (array of {Title, Result}). Returns counts and per-title results.
    /// </summary>
    internal static (int Installed, int Failed, IReadOnlyDictionary<string, string> Results)
        ParseInstallResults(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return (0, 0, map);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : new[] { root }.AsEnumerable();

            foreach (var el in items)
            {
                var title = el.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
                var result = el.TryGetProperty("Result", out var r) ? r.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(title))
                    map[title] = result;
            }
        }
        catch (JsonException ex)
        {
            Log.Warning("Failed to parse install result JSON: {Error}", ex.Message);
            return (0, 0, map);
        }

        var installed = map.Values.Count(IsInstalledResult);
        var failed = map.Values.Count(IsFailedResult);
        return (installed, failed, map);
    }

    private static bool IsInstalledResult(string result) =>
        result.Contains("Installed", StringComparison.OrdinalIgnoreCase) ||
        result.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) ||
        result.Equals("Success", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedResult(string result) =>
        result.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("Error", StringComparison.OrdinalIgnoreCase);

    private void ApplyInstallResults(IReadOnlyList<UpdateEntry> attempted, IReadOnlyDictionary<string, string> results)
    {
        foreach (var entry in attempted)
        {
            if (results.TryGetValue(entry.Title, out var result) && !string.IsNullOrWhiteSpace(result))
            {
                entry.Status = IsInstalledResult(result) ? "Installed"
                             : IsFailedResult(result) ? "Failed"
                             : result;
            }
            else
            {
                entry.Status = "Not applied";
            }
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var u in Updates) u.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var u in Updates) u.IsSelected = false;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void ParseUpdateJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : new[] { root }.AsEnumerable();

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
        }
        catch (JsonException ex)
        {
            Log.Warning("Failed to parse update JSON: {Error}", ex.Message);
            StatusMessage = "Parse error — some updates may not be shown.";
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
