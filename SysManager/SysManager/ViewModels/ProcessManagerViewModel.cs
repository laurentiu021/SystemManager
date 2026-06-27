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

    [ObservableProperty] private bool _isElevated;
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
        catch (InvalidOperationException ex) { Log.Warning("Process list auto-refresh failed: {Error}", ex.Message); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warning("Process list auto-refresh failed: {Error}", ex.Message); }

        _autoRefreshCts = new CancellationTokenSource();
        _ = AutoRefreshLoopAsync(_autoRefreshCts.Token);
    }

    private async Task AutoRefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
                if (!IsActive) continue;
                await RefreshAsync();
            }
            catch (OperationCanceledException) { break; /* expected on shutdown */ }
            // A single refresh fault (transient process/WMI/Win32 hiccup) must not kill
            // the loop permanently — log and keep polling, mirroring DashboardViewModel.
            catch (Exception ex) { Log.Debug("Process auto-refresh error: {Error}", ex.Message); }
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Refreshing process list…";

        try
        {
            // PERF-007: snapshot + enrichment on a background thread to avoid UI freezes.
            // Icon extraction (the expensive part) is done ONLY for processes not already
            // shown — see ReconcileInto, which is handed the set of PIDs we already track.
            var existingPids = Processes.Select(p => p.Pid).ToHashSet();
            var enriched = await Task.Run(async () =>
            {
                var snapshot = await _service.SnapshotAsync();
                foreach (var p in snapshot)
                {
                    // Only the volatile metrics change per tick; identity/description are
                    // stable, so enrich (and extract icons) just for newly-seen PIDs.
                    if (!existingPids.Contains(p.Pid))
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
                }
                return snapshot;
            });

            // Merge into the existing collection in place instead of replacing it. A
            // wholesale ReplaceWith raises a Reset that makes the DataGrid drop the user's
            // selection and scroll position every second; ReconcileInto keeps the surviving
            // ProcessEntry instances (so selection survives) and only adds/removes/updates.
            ReconcileInto(Processes, enriched);

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

    /// <summary>
    /// Merges <paramref name="snapshot"/> into <paramref name="target"/> in place, keyed by
    /// PID: surviving processes keep their existing <see cref="ProcessEntry"/> instance (with
    /// volatile metrics updated), new processes are added, and exited processes are removed.
    /// Preserving the instances is what lets the DataGrid keep the user's selection across a
    /// refresh. Newly-added entries from <paramref name="snapshot"/> are already enriched
    /// (icon/description) by the caller; surviving entries keep their existing icon/description.
    /// </summary>
    internal static void ReconcileInto(
        BulkObservableCollection<ProcessEntry> target,
        IReadOnlyList<ProcessEntry> snapshot)
    {
        var existing = target.ToDictionary(p => p.Pid);
        var seen = new HashSet<int>(snapshot.Count);

        foreach (var fresh in snapshot)
        {
            seen.Add(fresh.Pid);
            if (existing.TryGetValue(fresh.Pid, out var current))
            {
                // Update only the volatile metrics on the existing instance; identity fields
                // (Name, FilePath, Icon, PlainDescription, Category, SafetyLevel, StartTime)
                // are stable for a given PID and stay as they were.
                current.CpuPercent = fresh.CpuPercent;
                current.MemoryBytes = fresh.MemoryBytes;
                current.ThreadCount = fresh.ThreadCount;
                current.Status = fresh.Status;
                current.HasMainWindow = fresh.HasMainWindow;
            }
            else
            {
                target.Add(fresh);
            }
        }

        // Remove processes that no longer exist. Iterate a snapshot of the indices so we can
        // mutate target safely.
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(target[i].Pid))
                target.RemoveAt(i);
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
        var desired = source.OrderByDescending(p => p.MemoryBytes).ToList();

        // Sync the bound collection in place (Insert/Move/Remove) instead of ReplaceWith,
        // which raises a Reset. A Reset on the 1 Hz auto-refresh would drop the user's row
        // selection every second; an in-place sync keeps the surviving instances and their
        // selection intact.
        SyncOrdered(FilteredProcesses, desired);

        ProcessCount = FilteredProcesses.Count;
        TotalMemory = FilteredProcesses.Sum(p => p.MemoryBytes);
        Summary = $"{ProcessCount} processes · {FormatSize(TotalMemory)} total memory";
    }

    /// <summary>
    /// Makes <paramref name="target"/> match <paramref name="desired"/> in both membership
    /// and order using in-place Insert/Move/Remove operations (never a Reset), so a
    /// DataGrid bound to it keeps the user's selection and scroll position. Items are matched
    /// by reference, so surviving <see cref="ProcessEntry"/> instances are reused.
    /// </summary>
    internal static void SyncOrdered(
        BulkObservableCollection<ProcessEntry> target,
        IReadOnlyList<ProcessEntry> desired)
    {
        // Remove items that are no longer desired (walk backwards so indices stay valid).
        var desiredSet = new HashSet<ProcessEntry>(desired);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(target[i]))
                target.RemoveAt(i);
        }

        // Insert/move each desired item into its target position.
        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var currentIndex = target.IndexOf(item);
            if (currentIndex < 0)
                target.Insert(i, item);
            else if (currentIndex != i)
                target.Move(currentIndex, i);
        }
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
