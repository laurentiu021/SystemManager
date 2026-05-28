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
/// Context Menu Manager tab — lists all shell context menu entries and
/// lets the user enable/disable them non-destructively using LegacyDisable.
/// Supports presets for quick configuration.
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
    [ObservableProperty] private string _activePresetId = "";
    [ObservableProperty] private string _presetDescription = "";

    /// <summary>Available location filters for the ComboBox.</summary>
    public ObservableCollection<string> LocationFilters { get; } = new()
    {
        "All", "Files", "Folders", "Directory Background", "Desktop"
    };

    /// <summary>Available presets for the preset bar.</summary>
    public IReadOnlyList<ContextMenuPreset> Presets { get; } = [.. ContextMenuPreset.All.Values];

    public ContextMenuViewModel(ContextMenuService service)
    {
        _service = service;
        IsClassicMenuEnabled = ContextMenuService.IsClassicMenuEnabled();
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
            foreach (var item in items.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
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
            ActivePresetId = "";
            PresetDescription = "";
            UpdateCounts();
            StatusMessage = $"{entry.Name} {(desiredState ? "enabled" : "disabled")}.";
            Log.Information("Context menu entry toggled: {Name} -> {State}", entry.Name, desiredState ? "enabled" : "disabled");
        }
        else
        {
            entry.IsEnabled = !desiredState;
            StatusMessage = $"Could not toggle {entry.Name} — requires administrator privileges.";
        }
    }

    [RelayCommand]
    private void ApplyPreset(object? parameter)
    {
        if (parameter is not string presetId) return;
        if (!ContextMenuPreset.All.TryGetValue(presetId, out var preset)) return;

        var enableCount = 0;
        var disableCount = 0;
        foreach (var entry in _allEntries)
        {
            if (entry.IsSystemEntry) continue;
            if (preset.ShouldEnable(entry)) enableCount++;
            else disableCount++;
        }

        var needsRestart = preset.ForcesClassicMenu != IsClassicMenuEnabled;
        var message = $"This will enable {enableCount} entries and disable {disableCount} entries.";
        if (needsRestart)
            message += "\n\nThis also requires restarting Explorer — all open File Explorer windows will close.";
        message += "\n\nContinue?";

        if (!DialogService.Instance.Confirm(message, $"Apply \"{preset.Name}\" Preset"))
            return;

        ApplyPresetInternal(preset);
    }

    private void ApplyPresetInternal(ContextMenuPreset preset)
    {
        IsBusy = true;
        StatusMessage = $"Applying \"{preset.Name}\" preset...";

        try
        {
            // Apply menu style
            if (preset.ForcesClassicMenu && !IsClassicMenuEnabled)
            {
                ContextMenuService.EnableClassicMenu();
                IsClassicMenuEnabled = true;
            }
            else if (!preset.ForcesClassicMenu && IsClassicMenuEnabled)
            {
                ContextMenuService.DisableClassicMenu();
                IsClassicMenuEnabled = false;
            }

            // Apply entry states
            var changed = 0;
            foreach (var entry in _allEntries)
            {
                if (entry.IsSystemEntry) continue;

                var shouldEnable = preset.ShouldEnable(entry);
                if (shouldEnable == entry.IsEnabled) continue;

                bool success;
                if (shouldEnable)
                    success = _service.EnableEntry(entry);
                else
                    success = _service.DisableEntry(entry);

                if (success) changed++;
            }

            // Restart Explorer if menu style changed
            if (preset.ForcesClassicMenu != ContextMenuService.IsClassicMenuEnabled())
            {
                // Style was already set above, just need the restart
            }

            var needsRestart = preset.ForcesClassicMenu != ContextMenuService.IsClassicMenuEnabled();
            if (preset.Id is "win10" or "win11")
                ContextMenuService.RestartExplorer();

            ActivePresetId = preset.Id;
            PresetDescription = preset.Description;
            ApplyFilter();
            StatusMessage = $"\"{preset.Name}\" applied — {changed} entries changed.";
            ToastService.Instance.Show("Preset Applied", $"\"{preset.Name}\" — {changed} entries changed");
            Log.Information("Context menu preset applied: {Preset}, {Changed} entries changed", preset.Name, changed);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            StatusMessage = $"Preset failed: {ex.Message}";
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
