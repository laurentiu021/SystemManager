// SysManager · ContextMenuViewModel — manage Explorer right-click menu entries
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.IO;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Context Menu Manager tab — lists all shell context menu entries grouped
/// by location, lets the user toggle them, switch between Win10/Win11 style,
/// and customize which entries appear on right-click.
/// </summary>
public sealed partial class ContextMenuViewModel : ViewModelBase
{
    private readonly ContextMenuService _service;
    private List<ContextMenuEntry> _allEntries = [];

    public BulkObservableCollection<ContextMenuEntry> Entries { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _selectedLocation = "All";
    [ObservableProperty] private bool _showSystemEntries;
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private int _disabledCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _isClassicMenuEnabled;
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _activePresetId = "";
    [ObservableProperty] private string _presetDescription = "";

    /// <summary>Available location filters for the ComboBox.</summary>
    public ObservableCollection<string> LocationFilters { get; } = new()
    {
        "All", "Files", "Folders", "Directory Background", "Desktop"
    };

    public ContextMenuViewModel(ContextMenuService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        IsClassicMenuEnabled = ContextMenuService.IsClassicMenuEnabled();
        ActivePresetId = IsClassicMenuEnabled ? "win10" : "win11";
        PresetDescription = IsClassicMenuEnabled
            ? ContextMenuPreset.All["win10"].Description
            : ContextMenuPreset.All["win11"].Description;
        InitializeAsync(InitAsync);
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnSelectedLocationChanged(string value) => ApplyFilter();
    partial void OnShowSystemEntriesChanged(bool value) => ApplyFilter();

    private async Task InitAsync()
    {
        try { await ScanAsync(); }
        catch (InvalidOperationException ex) { Log.Warning("Context menu auto-scan failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warning("Context menu auto-scan failed: {Error}", ex.Message); }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        StatusMessage = "Scanning context menu entries...";
        try
        {
            var items = await Task.Run(() => _service.ScanEntries());

            _allEntries.Clear();
            foreach (var item in items.OrderBy(e => e.Location, StringComparer.OrdinalIgnoreCase)
                                      .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                _allEntries.Add(item);

            IsClassicMenuEnabled = ContextMenuService.IsClassicMenuEnabled();
            ApplyFilter();
            StatusMessage = $"Found {_allEntries.Count} context menu entries.";
            ToastService.Instance.Show("Context Menu scan complete", $"Found {_allEntries.Count} entries");
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void RelaunchElevated()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand]
    private void ToggleEntry(object? parameter)
    {
        if (parameter is not ContextMenuEntry entry) return;

        var desiredState = entry.IsEnabled;
        bool success;

        if (desiredState)
            success = _service.EnableEntry(entry);
        else
            success = _service.DisableEntry(entry);

        if (success)
        {
            ActivePresetId = "custom";
            PresetDescription = ContextMenuPreset.All["custom"].Description;
            UpdateCounts();
            StatusMessage = $"{entry.Name} {(desiredState ? "enabled" : "disabled")}.";
            Log.Information("Context menu entry toggled: {Name} -> {State}", entry.Name, desiredState ? "enabled" : "disabled");
        }
        else
        {
            entry.IsEnabled = !desiredState;
            if (!IsElevated)
            {
                if (DialogService.Instance.Confirm(
                    $"Could not toggle \"{entry.Name}\" — this requires administrator privileges.\n\nRestart SysManager as administrator?",
                    "Admin Required"))
                {
                    RelaunchElevated();
                }
            }
            else
            {
                StatusMessage = $"Could not toggle {entry.Name} — protected by Windows (owned by TrustedInstaller).";
            }
        }
    }

    [RelayCommand]
    private void ApplyPreset(object? parameter)
    {
        if (parameter is not string presetId) return;
        if (presetId == "custom")
        {
            ActivePresetId = "custom";
            PresetDescription = ContextMenuPreset.All["custom"].Description;
            return;
        }

        if (!ContextMenuPreset.All.TryGetValue(presetId, out var preset)) return;

        var needsRestart = preset.ForcesClassicMenu != IsClassicMenuEnabled;
        if (!needsRestart)
        {
            ActivePresetId = presetId;
            PresetDescription = preset.Description;
            StatusMessage = $"\"{preset.Name}\" is already active.";
            return;
        }

        var message = $"Switch to {preset.Name} context menu style?\n\nThis requires restarting Explorer — all open File Explorer windows will close.";
        if (!DialogService.Instance.Confirm(message, $"Apply \"{preset.Name}\""))
            return;

        IsBusy = true;
        StatusMessage = $"Switching to \"{preset.Name}\"...";

        try
        {
            if (preset.ForcesClassicMenu)
                ContextMenuService.EnableClassicMenu();
            else
                ContextMenuService.DisableClassicMenu();

            ContextMenuService.RestartExplorer();
            IsClassicMenuEnabled = preset.ForcesClassicMenu;
            ActivePresetId = presetId;
            PresetDescription = preset.Description;
            StatusMessage = $"\"{preset.Name}\" applied — Explorer restarted.";
            ToastService.Instance.Show("Menu Style Changed", $"\"{preset.Name}\" applied");
            Log.Information("Context menu style changed to: {Preset}", preset.Name);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            StatusMessage = $"Failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task RefreshAsync() => ScanAsync();

    private void ApplyFilter()
    {
        var filtered = _allEntries.AsEnumerable();

        if (!ShowSystemEntries)
            filtered = filtered.Where(e => !e.IsSystemEntry);

        if (!string.Equals(SelectedLocation, "All", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(e => string.Equals(e.Location, SelectedLocation, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var text = FilterText;
            filtered = filtered.Where(e =>
                e.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                e.Command.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                e.Source.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                e.RawName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                e.Explanation.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        Entries.ReplaceWith(filtered);
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        EnabledCount = Entries.Count(e => e.IsEnabled);
        DisabledCount = Entries.Count(e => !e.IsEnabled);
        TotalCount = Entries.Count;
    }
}
