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
        // Re-evaluate the long-running commands' CanExecute when IsBusy flips. Scan,
        // Refresh and ApplyPreset all mutate the shared _allEntries list off the UI thread
        // (ApplyPreset also restarts Explorer); disabling them while one runs prevents
        // overlapping runs from corrupting that list or racing two Explorer restarts.
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(InitAsync);
    }

    /// <summary>
    /// Gate for the long-running commands so overlapping Scan/Refresh/ApplyPreset runs
    /// can't mutate <see cref="_allEntries"/> concurrently or race two Explorer restarts.
    /// The startup scan calls <see cref="ScanAsync"/> directly (not via the command) so it
    /// is unaffected by this gate.
    /// </summary>
    private bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        ScanCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        ApplyPresetCommand.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            PropertyChanged -= OnVmPropertyChanged;
        base.Dispose(disposing);
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

    [RelayCommand(CanExecute = nameof(NotBusy))]
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
    private void RelaunchAsAdmin()
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
                    RelaunchAsAdmin();
                }
            }
            else
            {
                StatusMessage = $"Could not toggle {entry.Name} — protected by Windows (owned by TrustedInstaller).";
            }
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ApplyPresetAsync(object? parameter)
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
        var thirdPartyEnabled = _allEntries.Count(e => !e.IsSystemEntry && e.IsEnabled && !IsDefaultWindowsEntry(e));

        var message = $"Apply {preset.Name}?";
        if (needsRestart)
            message += "\n\nThis requires restarting Explorer — all open File Explorer windows will close.";
        if (thirdPartyEnabled > 0)
            message += $"\n\n{thirdPartyEnabled} third-party entries (Git, NVIDIA, etc.) will be disabled to restore the clean default menu. You can re-enable any of them individually afterwards.";

        if (!DialogService.Instance.Confirm(message, $"Apply \"{preset.Name}\""))
            return;

        IsBusy = true;
        StatusMessage = $"Applying \"{preset.Name}\"...";

        try
        {
            // Registry writes + RestartExplorer are synchronous and can take a moment
            // (Explorer restart especially) — run them off the UI thread so the window
            // stays responsive. No UI state is touched inside the Task.Run body.
            var (disabled, enabled) = await Task.Run(() =>
            {
                if (needsRestart)
                {
                    if (preset.ForcesClassicMenu)
                        ContextMenuService.EnableClassicMenu();
                    else
                        ContextMenuService.DisableClassicMenu();
                }

                // Disable all third-party entries to restore clean default
                var dis = 0;
                foreach (var entry in _allEntries.Where(e =>
                             !e.IsSystemEntry && e.IsEnabled && !IsDefaultWindowsEntry(e)))
                {
                    if (_service.DisableEntry(entry))
                        dis++;
                }

                // Enable any default Windows entries that were previously disabled
                var en = 0;
                foreach (var entry in _allEntries.Where(e =>
                             !e.IsSystemEntry && !e.IsEnabled && IsDefaultWindowsEntry(e)))
                {
                    if (_service.EnableEntry(entry))
                        en++;
                }

                if (needsRestart)
                    ContextMenuService.RestartExplorer();

                return (dis, en);
            });

            IsClassicMenuEnabled = preset.ForcesClassicMenu;
            ActivePresetId = presetId;
            PresetDescription = preset.Description;
            ApplyFilter();

            var changes = disabled + enabled;
            StatusMessage = changes > 0
                ? $"\"{preset.Name}\" applied — {disabled} disabled, {enabled} re-enabled."
                : $"\"{preset.Name}\" applied.";
            ToastService.Instance.Show("Preset Applied", $"\"{preset.Name}\" — clean default restored");
            Log.Information("Context menu preset applied: {Preset}, {Disabled} disabled, {Enabled} enabled", preset.Name, disabled, enabled);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            StatusMessage = $"Failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
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

    private static readonly HashSet<string> DefaultWindowsRawNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "edit", "print", "runas", "runasuser", "find", "explore",
        "cmd", "powershell", "properties", "copy", "cut", "paste", "delete",
        "rename", "pintohomefile", "PinToStartScreen", "Windows.ModernShare",
        "opennewwindow", "opennewtab", "removeproperties", "EditStickers",
        "Troubleshoot compatibility"
    };

    private static readonly HashSet<string> DefaultWindowsSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Microsoft Windows", "Microsoft Corporation", "Windows Terminal"
    };

    private static bool IsDefaultWindowsEntry(ContextMenuEntry entry)
    {
        if (DefaultWindowsRawNames.Contains(entry.RawName))
            return true;
        if (!string.IsNullOrEmpty(entry.Source) && DefaultWindowsSources.Contains(entry.Source))
            return true;
        return false;
    }
}
