// SysManager · ProcessManagerViewModel — running process list with kill
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Process Manager tab — lists running processes with memory/thread info,
/// allows kill and open file location.
/// </summary>
public partial class ProcessManagerViewModel : ViewModelBase
{
    private readonly ProcessManagerService _service = new();

    public ObservableCollection<ProcessEntry> Processes { get; } = new();
    public ObservableCollection<ProcessEntry> FilteredProcesses { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private int _processCount;
    [ObservableProperty] private long _totalMemory;
    [ObservableProperty] private string _summary = "Click Refresh to list running processes.";
    [ObservableProperty] private string _sortBy = "Memory";

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public ProcessManagerViewModel()
    {
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Refreshing process list…";

        try
        {
            var snapshot = await _service.SnapshotAsync();
            Processes.Clear();
            foreach (var p in snapshot)
                Processes.Add(p);

            ApplyFilter();
            StatusMessage = $"Loaded {ProcessCount} processes.";
        }
        catch (Exception ex)
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
        if (entry == null) return;

        var result = System.Windows.MessageBox.Show(
            $"Are you sure you want to kill \"{entry.Name}\" (PID {entry.Pid})?\n\nThis may cause unsaved data loss.",
            "Kill process",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        var success = ProcessManagerService.KillProcess(entry.Pid);
        if (success)
        {
            Processes.Remove(entry);
            FilteredProcesses.Remove(entry);
            ApplyFilter();
            StatusMessage = $"Killed {entry.Name} (PID {entry.Pid}).";
        }
        else
        {
            StatusMessage = $"Could not kill {entry.Name} — may need admin rights.";
        }
    }

    [RelayCommand]
    private static void OpenFileLocation(ProcessEntry? entry)
    {
        if (entry == null) return;
        ProcessManagerService.OpenFileLocation(entry.FilePath);
    }

    [RelayCommand]
    private void SortByName() { SortBy = "Name"; ApplyFilter(); }

    [RelayCommand]
    private void SortByMemory() { SortBy = "Memory"; ApplyFilter(); }

    [RelayCommand]
    private void SortByCpu() { SortBy = "CPU"; ApplyFilter(); }

    [RelayCommand]
    private void SortByPid() { SortBy = "PID"; ApplyFilter(); }

    private void ApplyFilter()
    {
        FilteredProcesses.Clear();

        IEnumerable<ProcessEntry> source = Processes;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var filter = FilterText.Trim();
            source = source.Where(p =>
                p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString().Contains(filter));
        }

        source = SortBy switch
        {
            "Name" => source.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            "PID" => source.OrderBy(p => p.Pid),
            "CPU" => source.OrderByDescending(p => p.CpuPercent),
            _ => source.OrderByDescending(p => p.MemoryBytes)
        };

        foreach (var p in source)
            FilteredProcesses.Add(p);

        ProcessCount = FilteredProcesses.Count;
        TotalMemory = FilteredProcesses.Sum(p => p.MemoryBytes);
        Summary = $"{ProcessCount} processes · {FormatSize(TotalMemory)} total memory";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B"
    };
}
