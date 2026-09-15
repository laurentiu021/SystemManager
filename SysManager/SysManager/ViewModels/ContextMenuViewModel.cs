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
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    private readonly IContextMenuService _service;
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
    /// <remarks>
    /// Must cover every <see cref="ContextMenuEntry.Location"/> the scan can produce, or a row shows
    /// under "All" and then cannot be reached by any specific filter — which reads as the filter being
    /// broken. "Files and folders" is here for the <c>AllFilesystemObjects</c> shell-extension root:
    /// merging it into "Files" would be shorter and would misdescribe every folder the handler also
    /// applies to. <c>EveryScannedLocation_HasAFilterThatMatchesIt</c> pins the coverage.
    /// </remarks>
    public ObservableCollection<string> LocationFilters { get; } = new()
    {
        "All", "Files", "Folders", "Files and folders", "Directory Background", "Desktop"
    };

    public ContextMenuViewModel(IContextMenuService service)
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
        // RestartExplorer is in that set for the racing reason specifically: two overlapping
        // restarts can leave the user without a shell.
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
        RestartExplorerCommand.NotifyCanExecuteChanged();
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
        IsProgressIndeterminate = true;
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
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }

    [RelayCommand]
    private async Task ToggleEntryAsync(object? parameter)
    {
        if (parameter is not ContextMenuEntry entry) return;

        var desiredState = entry.IsEnabled;

        var success = await Task.Run(() => desiredState
            ? _service.EnableEntry(entry)
            : _service.DisableEntry(entry));

        if (success)
        {
            ActivePresetId = "custom";
            PresetDescription = ContextMenuPreset.All["custom"].Description;
            UpdateCounts();

            // A verb change shows up on the next right-click; a handler change does not, because Explorer
            // decided which handlers to load when it started. Saying so is the difference between "the
            // toggle worked" and "the toggle did nothing", which is what it looks like otherwise.
            StatusMessage = entry.Kind == ContextMenuEntryKind.Handler
                ? $"{entry.Name} {(desiredState ? "allowed" : "blocked")} — restart Explorer to see the change."
                : $"{entry.Name} {(desiredState ? "enabled" : "disabled")}.";
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
                // Already elevated, so the cause is not permission. Naming TrustedInstaller for a handler
                // would be a guess at the wrong mechanism — it is not what guards the Blocked list.
                StatusMessage = entry.Kind == ContextMenuEntryKind.Handler
                    ? $"Could not change {entry.Name} — Windows would not accept the change to its blocked add-ons list."
                    : $"Could not toggle {entry.Name} — protected by Windows (owned by TrustedInstaller).";
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
        var thirdPartyEnabled = _allEntries.Count(IsPresetTarget);

        var message = $"Apply {preset.Name}?";
        if (needsRestart)
            message += "\n\nThis requires restarting Explorer — all open File Explorer windows will close.";
        if (thirdPartyEnabled > 0)
            message += $"\n\n{thirdPartyEnabled} third-party entries (Git, NVIDIA, etc.) will be disabled to restore the clean default menu. You can re-enable any of them individually afterwards.";

        if (!DialogService.Instance.Confirm(message, $"Apply \"{preset.Name}\""))
            return;

        IsBusy = true;
        IsProgressIndeterminate = true;
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
                foreach (var entry in _allEntries.Where(IsPresetTarget))
                {
                    if (_service.DisableEntry(entry))
                        dis++;
                }

                // Enable any default Windows entries that were previously disabled
                var en = 0;
                foreach (var entry in _allEntries.Where(IsPresetRestorable))
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
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task RefreshAsync() => ScanAsync();

    /// <summary>
    /// Restarts Explorer so a blocked or allowed add-on takes effect.
    /// </summary>
    /// <remarks>
    /// Needed because Explorer reads the Blocked list when it loads a handler, so a change to it is
    /// invisible until then. Without this the tab would report "blocked" while the add-on stayed in the
    /// menu, and the user's only correct conclusion would be that the toggle does not work.
    /// <para>Confirmed first: it closes every open File Explorer window, which is disruptive enough that
    /// it must not happen from a single click. Preset application restarts Explorer too, but there it is
    /// part of a change the user already confirmed.</para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RestartExplorerAsync()
    {
        if (!DialogService.Instance.Confirm(
                "Restart Windows Explorer now?\n\nAll open File Explorer windows will close. Your files are "
                + "not affected, and the desktop comes back on its own. This is what applies changes to "
                + "add-ons in the list below.",
                "Restart Explorer"))
            return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Restarting Explorer...";

        try
        {
            await Task.Run(ContextMenuService.RestartExplorer);
            StatusMessage = "Explorer restarted — right-click menu changes are now in effect.";
            ToastService.Instance.Show("Explorer restarted", "Right-click menu changes are now in effect");
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

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

    /// <summary>
    /// A row a preset is allowed to touch at all: an ordinary verb, never a shell-extension handler.
    /// </summary>
    /// <remarks>
    /// The <c>Kind</c> clause is the load-bearing one, and it stayed load-bearing when handlers became
    /// toggleable — for a different reason than before. Previously a preset would have called
    /// <c>DisableEntry</c> on every handler and the write would have been refused, so the dialog promised
    /// a count it could not deliver. Now the write would SUCCEED, and one click on "Win11 Default" would
    /// block every third-party shell extension on the machine: an HKLM change, needing elevation, that
    /// takes out archive menus, cloud-sync overlays and antivirus scan items together. That is far more
    /// than the button says it does.
    /// <para>So a handler is hidden one row at a time, deliberately, and a preset stays what it claims to
    /// be — a menu-style change. The two preset queries go through here so the confirmation's count and
    /// the loop's selection cannot drift apart (#1510).</para>
    /// </remarks>
    private static bool IsPresetEligible(ContextMenuEntry entry) =>
        entry.Kind == ContextMenuEntryKind.MenuEntry && !entry.IsSystemEntry;

    /// <summary>A row a preset switches off: eligible, currently on, and not a Windows default.</summary>
    private static bool IsPresetTarget(ContextMenuEntry entry) =>
        IsPresetEligible(entry) && entry.IsEnabled && !IsDefaultWindowsEntry(entry);

    /// <summary>A row a preset switches back on: eligible, currently off, and a Windows default.</summary>
    private static bool IsPresetRestorable(ContextMenuEntry entry) =>
        IsPresetEligible(entry) && !entry.IsEnabled && IsDefaultWindowsEntry(entry);

    private static bool IsDefaultWindowsEntry(ContextMenuEntry entry)
    {
        if (DefaultWindowsRawNames.Contains(entry.RawName))
            return true;
        if (!string.IsNullOrEmpty(entry.Source) && DefaultWindowsSources.Contains(entry.Source))
            return true;
        return false;
    }
}
