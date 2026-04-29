// SysManager · SystemHealthViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

public partial class SystemHealthViewModel : ViewModelBase
{
    private readonly SystemInfoService _sys;
    private readonly DiskHealthService _diskHealth = new();
    private readonly MemoryTestService _memTest = new();
    private readonly FixedDriveService _drives = new();
    private readonly PowerShellRunner _runner = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<MemoryModule> Modules { get; } = new();
    public ObservableCollection<DiskInfo> Disks { get; } = new();
    public ObservableCollection<DiskHealthReport> DiskHealth { get; } = new();
    public ObservableCollection<DriveTarget> ChkdskDrives { get; } = new();

    public ConsoleViewModel Console { get; } = new();

    [ObservableProperty] private OsInfo? _os;
    [ObservableProperty] private CpuInfo? _cpu;
    [ObservableProperty] private MemoryInfo? _memory;
    [ObservableProperty] private string _summary = "Press 'Scan' to collect system info";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private bool _isChkdskRunning;
    [ObservableProperty] private string _chkdskStatus = string.Empty;

    // Memory diagnostic summary
    [ObservableProperty] private int _wheaMemoryErrors;
    [ObservableProperty] private int _memoryDiagnosticResults;
    [ObservableProperty] private string _memoryHealthVerdict = "Click 'Check memory errors' to inspect.";
    [ObservableProperty] private string _memoryHealthColorHex = "#9AA0A6";

    public SystemHealthViewModel(SystemInfoService sys)
    {
        _sys = sys;
        IsElevated = AdminHelper.IsElevated();
        _runner.LineReceived += l => Console.Append(l);

        // Kick off drive discovery in the background so the list is ready
        // when the user opens the tab.
        _ = RefreshDrivesAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Collecting system info...";
        try
        {
            var snap = await _sys.CaptureAsync();
            Os = snap.Os;
            Cpu = snap.Cpu;
            Memory = snap.Memory;
            Modules.Clear();
            foreach (var m in snap.Memory.Modules) Modules.Add(m);
            Disks.Clear();
            foreach (var d in snap.Disks) Disks.Add(d);
            Summary = $"OS {snap.Os.Caption}  —  CPU {snap.Cpu.Name} ({snap.Cpu.Cores}c/{snap.Cpu.LogicalProcessors}t)  —  RAM {snap.Memory.UsedGB:0.0}/{snap.Memory.TotalGB:0.0} GB  —  Disks {snap.Disks.Count}";
            StatusMessage = $"Scan at {snap.CapturedAt:HH:mm:ss}";
            Log.Information("System health scan completed");
            await RefreshDrivesAsync();
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private async Task RefreshDrivesAsync()
    {
        try
        {
            var list = await _drives.EnumerateAsync();
            ChkdskDrives.Clear();
            foreach (var d in list)
            {
                ChkdskDrives.Add(new DriveTarget
                {
                    Letter = d.Letter,
                    Label = d.Label,
                    FileSystem = d.FileSystem,
                    SizeGB = d.SizeGB,
                    FreeGB = d.FreeGB,
                    MediaType = d.MediaType,
                    BusType = d.BusType,
                    IsSelected = string.Equals(d.Letter, "C:", StringComparison.OrdinalIgnoreCase),
                    Status = "Idle"
                });
            }
        }
        catch (Exception ex) { StatusMessage = $"Drive enumeration failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task CheckDiskHealthAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Reading SMART data...";
        DiskHealth.Clear();
        try
        {
            var reports = await _diskHealth.CollectAsync();
            foreach (var r in reports) DiskHealth.Add(r);
            StatusMessage = $"Collected {reports.Count} disk report(s).";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private async Task CheckMemoryErrorsAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Scanning event log for memory errors...";
        try
        {
            var summary = await _memTest.CheckErrorLogsAsync();
            WheaMemoryErrors = summary.WheaMemoryErrors;
            MemoryDiagnosticResults = summary.MemoryDiagnosticResults;

            if (summary.WheaMemoryErrors > 0)
            {
                MemoryHealthVerdict = $"{summary.WheaMemoryErrors} hardware-error event(s) in the last 30 days. Test your RAM.";
                MemoryHealthColorHex = "#EF4444";
            }
            else if (summary.MemoryDiagnosticResults > 0)
            {
                MemoryHealthVerdict = $"Memory diagnostic has run {summary.MemoryDiagnosticResults} time(s) recently. Check results.";
                MemoryHealthColorHex = "#F59E0B";
            }
            else
            {
                MemoryHealthVerdict = "No memory errors reported in the last 30 days.";
                MemoryHealthColorHex = "#22C55E";
            }
            StatusMessage = "Memory scan done.";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private void ScheduleMemoryTest()
    {
        var ok = _memTest.ScheduleAtNextBoot();
        StatusMessage = ok
            ? "Windows Memory Diagnostic launched — choose a schedule option."
            : "Failed to launch mdsched.exe.";
    }

    [RelayCommand]
    private async Task RunChkdskAsync(string? driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
        {
            StatusMessage = "No drive specified.";
            return;
        }

        var target = ChkdskDrives.FirstOrDefault(d => string.Equals(d.Letter, driveLetter, StringComparison.OrdinalIgnoreCase));
        IsChkdskRunning = true;
        _cts = new CancellationTokenSource();
        try
        {
            await RunChkdskCoreAsync(driveLetter, target, _cts.Token);
        }
        finally { IsChkdskRunning = false; }
    }

    [RelayCommand]
    private async Task RunChkdskOnSelectedAsync()
    {
        var selected = ChkdskDrives.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Select at least one drive.";
            return;
        }

        if (!AdminHelper.IsElevated())
        {
            StatusMessage = "chkdsk requires admin privileges. Click 'Grant admin privileges' to elevate.";
            foreach (var d in selected) d.Status = "Needs admin";
            return;
        }

        IsChkdskRunning = true;
        ChkdskStatus = $"Scanning {selected.Count} drive(s)...";
        _cts = new CancellationTokenSource();
        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                if (_cts.Token.IsCancellationRequested) break;
                var d = selected[i];
                ChkdskStatus = $"[{i + 1}/{selected.Count}] Scanning {d.Letter} — {d.Label}";
                await RunChkdskCoreAsync(d.Letter, d, _cts.Token);
            }
            ChkdskStatus = _cts.Token.IsCancellationRequested ? "Cancelled." : "All scans finished.";
        }
        finally { IsChkdskRunning = false; }
    }

    private async Task RunChkdskCoreAsync(string driveLetter, DriveTarget? target, CancellationToken ct)
    {
        // chkdsk /scan requires admin privileges — fail fast with a clear
        // message instead of running and reporting a cryptic exit code.
        if (!AdminHelper.IsElevated())
        {
            var msg = $"chkdsk {driveLetter} requires admin privileges. Click 'Grant admin privileges' to elevate.";
            StatusMessage = msg;
            if (target != null) target.Status = "Needs admin";
            return;
        }

        StatusMessage = $"Running chkdsk {driveLetter} (read-only)...";
        if (target != null) target.Status = "Running...";
        try
        {
            // Capture chkdsk output lines so we can parse the verdict from
            // the text rather than relying solely on the exit code. chkdsk
            // may return non-zero even on healthy disks (e.g. when the
            // volume is in use or /scan is not supported on the filesystem).
            var captured = new System.Collections.Generic.List<string>();
            void OnLine(PowerShellLine l) => captured.Add(l.Text);
            _runner.LineReceived += OnLine;

            var oemEncoding = System.Text.Encoding.GetEncoding(
                System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            int exit;
            try
            {
                exit = await _runner.RunProcessAsync("chkdsk.exe", $"{driveLetter} /scan", ct, oemEncoding);
            }
            finally { _runner.LineReceived -= OnLine; }

            var verdict = ParseChkdskVerdict(captured, exit);
            if (target != null) target.Status = verdict;
            StatusMessage = $"chkdsk {driveLetter} done — {verdict}.";
            Log.Information("chkdsk completed on {Drive}: exit {ExitCode}, verdict {Verdict}", driveLetter, exit, verdict);
        }
        catch (OperationCanceledException)
        {
            if (target != null) target.Status = "Cancelled";
            StatusMessage = $"chkdsk {driveLetter} cancelled.";
        }
        catch (Exception ex)
        {
            if (target != null) target.Status = "Error";
            StatusMessage = $"Error on {driveLetter}: {ex.Message}";
        }
    }

    /// <summary>
    /// Parses chkdsk output to determine the health verdict. The exit code
    /// alone is unreliable — chkdsk may return non-zero on healthy volumes
    /// when the volume is in use or /scan is not supported.
    /// </summary>
    internal static string ParseChkdskVerdict(IReadOnlyList<string> outputLines, int exitCode)
    {
        var combined = string.Join('\n', outputLines);

        // Healthy patterns (English + common localized variants)
        if (combined.Contains("found no problems", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("no further action is required", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("no errors", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("appears to be healthy", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("no problems were found", StringComparison.OrdinalIgnoreCase))
        {
            return "Healthy";
        }

        // Errors found but corrected
        if (combined.Contains("made corrections", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("corrected errors", StringComparison.OrdinalIgnoreCase))
        {
            return "Repaired";
        }

        // /scan not supported (FAT32, exFAT)
        if (combined.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("cannot run", StringComparison.OrdinalIgnoreCase))
        {
            return "Not supported";
        }

        // Fall back to exit code
        return exitCode == 0 ? "Healthy" : $"Exit {exitCode}";
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();
}

/// <summary>
/// A fixed drive shown in the chkdsk selector. Mutable so the UI can reflect
/// live status ("Running...", "OK", "Error") as scans progress.
/// </summary>
public partial class DriveTarget : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = "Idle";
    public string Letter { get; init; } = "C:";
    public string Label { get; init; } = "";
    public string FileSystem { get; init; } = "NTFS";
    public double SizeGB { get; init; }
    public double FreeGB { get; init; }
    public string MediaType { get; init; } = "";
    public string BusType { get; init; } = "";

    public string Display => string.IsNullOrWhiteSpace(Label) || Label == Letter
        ? $"{Letter}  ·  {SizeGB:F0} GB {FileSystem}"
        : $"{Letter}  {Label}  ·  {SizeGB:F0} GB {FileSystem}";
}
