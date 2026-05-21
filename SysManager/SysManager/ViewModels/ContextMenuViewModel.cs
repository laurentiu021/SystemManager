// SysManager · ContextMenuViewModel — manage Explorer right-click menu entries
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
/// Context Menu Manager tab — lists all shell context menu entries and
/// lets the user enable/disable them non-destructively using LegacyDisable.
/// </summary>
public sealed partial class ContextMenuViewModel : ViewModelBase
{
    private readonly ContextMenuService _service;
    private List<ContextMenuEntry> _allEntries = [];

    public BulkObservableCollection<ContextMenuEntry> Entries { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _selectedLocation = "All";
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private int _disabledCount;
    [ObservableProperty] private int _totalCount;

    /// <summary>Available location filters for the ComboBox.</summary>
    public ObservableCollection<string> LocationFilters { get; } = new()
    {
        "All", "Files", "Folders", "Directory Background", "Desktop"
    };

    public ContextMenuViewModel(ContextMenuService service)
    {
        _service = service;
        InitializeAsync(InitAsync);
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnSelectedLocationChanged(string value) => ApplyFilter();

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

            ApplyFilter();
            StatusMessage = $"Found {_allEntries.Count} context menu entries.";
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

        // The CheckBox two-way binding has already flipped IsEnabled before
        // this command runs. We use the current (already-flipped) value as
        // the desired new state.
        var desiredState = entry.IsEnabled;
        bool success;

        if (desiredState)
            success = _service.EnableEntry(entry);
        else
            success = _service.DisableEntry(entry);

        if (success)
        {
            UpdateCounts();
            StatusMessage = $"{entry.Name} {(desiredState ? "enabled" : "disabled")}.";
            Log.Information("Context menu entry toggled: {Name} -> {State}", entry.Name, desiredState ? "enabled" : "disabled");
        }
        else
        {
            // Revert the CheckBox state since the operation failed
            entry.IsEnabled = !desiredState;
            StatusMessage = $"Could not toggle {entry.Name} — requires administrator privileges.";
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => ScanAsync();

    private void ApplyFilter()
    {
        var filtered = _allEntries.AsEnumerable();

        // Location filter
        if (!string.Equals(SelectedLocation, "All", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(e => string.Equals(e.Location, SelectedLocation, StringComparison.OrdinalIgnoreCase));

        // Text filter
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var text = FilterText;
            filtered = filtered.Where(e =>
                e.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                e.Command.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                e.Source.Contains(text, StringComparison.OrdinalIgnoreCase));
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
