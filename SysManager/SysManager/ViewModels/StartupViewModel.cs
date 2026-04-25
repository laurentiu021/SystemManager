// SysManager · StartupViewModel — manage programs that run at boot
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Startup Manager tab — lists all programs that run at Windows boot
/// and lets the user enable/disable them non-destructively.
/// </summary>
public partial class StartupViewModel : ViewModelBase
{
    private readonly StartupService _service = new();

    public ObservableCollection<StartupEntry> Entries { get; } = new();

    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private int _disabledCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _scanSummary = "Click Scan to discover startup items.";

    public StartupViewModel()
    {
        // Auto-scan on construction
        _ = ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        StatusMessage = "Scanning startup items…";
        try
        {
            var items = await _service.ScanAsync();
            Entries.Clear();
            foreach (var item in items.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                Entries.Add(item);

            UpdateCounts();
            StatusMessage = $"Found {TotalCount} startup items.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ToggleEntry(StartupEntry? entry)
    {
        if (entry == null) return;
        var newState = !entry.IsEnabled;
        var success = StartupService.SetEnabled(entry, newState);
        if (success)
        {
            UpdateCounts();
            StatusMessage = $"{entry.Name} {(newState ? "enabled" : "disabled")}.";
        }
        else
        {
            StatusMessage = $"Could not toggle {entry.Name} — {entry.StatusText}";
        }
    }

    [RelayCommand]
    private void EnableAll()
    {
        foreach (var entry in Entries.Where(e => !e.IsEnabled))
            StartupService.SetEnabled(entry, true);
        UpdateCounts();
        StatusMessage = "All items enabled.";
    }

    [RelayCommand]
    private void OpenFileLocation(StartupEntry? entry)
    {
        if (entry == null) return;
        try
        {
            var path = entry.Command.Trim('"', ' ');
            var spaceIdx = path.IndexOf(' ');
            if (spaceIdx > 0 && !System.IO.File.Exists(path))
                path = path[..spaceIdx].Trim('"');

            if (System.IO.File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
        }
        catch { StatusMessage = "Could not open file location."; }
    }

    private void UpdateCounts()
    {
        EnabledCount = Entries.Count(e => e.IsEnabled);
        DisabledCount = Entries.Count(e => !e.IsEnabled);
        TotalCount = Entries.Count;
        ScanSummary = $"{EnabledCount} enabled · {DisabledCount} disabled · {TotalCount} total";
    }
}
