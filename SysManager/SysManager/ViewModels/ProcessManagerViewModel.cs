// SysManager · ProcessManagerViewModel — running process list with kill
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Process Manager tab — lists running processes with memory/thread info,
/// allows kill and open file location.
/// </summary>
public sealed partial class ProcessManagerViewModel : ViewModelBase
{
    private readonly ProcessManagerService _service;
    private CancellationTokenSource? _autoRefreshCts;

    public BulkObservableCollection<ProcessEntry> Processes { get; } = new();
    public BulkObservableCollection<ProcessEntry> FilteredProcesses { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _showOnlyApps;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private int _processCount;
    [ObservableProperty] private long _totalMemory;
    [ObservableProperty] private string _summary = "Click Refresh to list running processes.";

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnShowOnlyAppsChanged(bool value) => ApplyFilter();

    public ProcessManagerViewModel(ProcessManagerService service)
    {
        _service = service;
        InitializeAsync(InitAsync);
    }

    private async Task InitAsync()
    {
        try { await RefreshAsync(); }
        catch (InvalidOperationException ex) { Log.Warning("Process list auto-refresh failed: {Error}", ex.Message); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warning("Process list auto-refresh failed: {Error}", ex.Message); }

        _autoRefreshCts = new CancellationTokenSource();
        _ = AutoRefreshLoopAsync(_autoRefreshCts.Token);
    }

    private async Task AutoRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                if (!IsActive) continue;
                await RefreshAsync();
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Refreshing process list…";

        try
        {
            // PERF-007: Perform snapshot, icon extraction, and description lookup
            // on a background thread to avoid UI freezes on slow processes.
            var enriched = await Task.Run(async () =>
            {
                var snapshot = await _service.SnapshotAsync();
                foreach (var p in snapshot)
                {
                    p.Icon = IconExtractorService.GetProcessIcon(p.FilePath, p.Name);
                    var dbEntry = ProcessDescriptionService.Instance.Lookup(p.Name);
                    if (dbEntry is not null)
                    {
                        p.PlainDescription = dbEntry.Description;
                        p.Category = dbEntry.Category;
                        p.SafetyLevel = dbEntry.Safety.ToString();
                    }
                    else
                    {
                        p.PlainDescription = p.Description;
                        p.Category = "Unknown";
                        p.SafetyLevel = "Unknown";
                    }
                }
                return snapshot;
            });

            Processes.ReplaceWith(enriched);

            ApplyFilter();
            StatusMessage = $"Loaded {ProcessCount} processes.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Failed: {ex.Message}";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            StatusMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void KillProcess(ProcessEntry? entry)
    {
        if (entry is null) return;

        if (!DialogService.Instance.Confirm(
            $"Are you sure you want to kill \"{entry.Name}\" (PID {entry.Pid})?\n\nThis may cause unsaved data loss.",
            "Kill process")) return;

        var success = ProcessManagerService.KillProcess(entry.Pid);
        if (success)
        {
            Processes.Remove(entry);
            FilteredProcesses.Remove(entry);
            ApplyFilter();
            StatusMessage = $"Killed {entry.Name} (PID {entry.Pid}).";
            Log.Information("Process killed: PID {Pid}", entry.Pid);
        }
        else
        {
            StatusMessage = $"Could not kill {entry.Name} — may need admin rights.";
            Log.Warning("Failed to kill process PID {Pid}", entry.Pid);
        }
    }

    [RelayCommand]
    private static void OpenFileLocation(ProcessEntry? entry)
    {
        if (entry is null || !entry.CanOpenFileLocation) return;
        ProcessManagerService.OpenFileLocation(entry.FilePath);
    }

    private void ApplyFilter()
    {
        IEnumerable<ProcessEntry> source = Processes;

        if (ShowOnlyApps)
            source = source.Where(p => p.HasMainWindow);

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var filter = FilterText.Trim();
            source = source.Where(p => MatchesFilter(p, filter));
        }

        // Default order by memory descending; DataGrid column headers handle user sorting.
        FilteredProcesses.ReplaceWith(source.OrderByDescending(p => p.MemoryBytes));

        ProcessCount = FilteredProcesses.Count;
        TotalMemory = FilteredProcesses.Sum(p => p.MemoryBytes);
        Summary = $"{ProcessCount} processes · {FormatSize(TotalMemory)} total memory";
    }

    private static string FormatSize(long bytes) => Helpers.FormatHelper.FormatSize(bytes);

    private static bool MatchesFilter(ProcessEntry p, string filter) =>
        MatchesName(p, filter) || MatchesDescription(p, filter) || MatchesPid(p, filter);

    private static bool MatchesName(ProcessEntry p, string filter) =>
        p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesDescription(ProcessEntry p, string filter)
    {
        ReadOnlySpan<string?> fields = [p.Description, p.PlainDescription, p.Category];
        foreach (var field in fields)
        {
            if (field?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        return false;
    }

    private static bool MatchesPid(ProcessEntry p, string filter) =>
        p.Pid.ToString().Contains(filter);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoRefreshCts?.Cancel();
            _autoRefreshCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
