// SysManager · DefenderViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Defender Tweaks tab. Shows the current Microsoft Defender status,
/// toggles PUA protection and Controlled Folder Access, and manages scan exclusion
/// folders. Every change requires admin and is verified by reading the value back
/// (Tamper Protection can silently reject it); changes are confirmed first.
/// </summary>
public sealed partial class DefenderViewModel : ViewModelBase
{
    private readonly DefenderService _service;

    public BulkObservableCollection<string> ExclusionPaths { get; } = new();

    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private bool _isTamperProtected;
    [ObservableProperty] private string _realtimeDisplay = "—";
    [ObservableProperty] private string _puaDisplay = "—";
    [ObservableProperty] private string _cfaDisplay = "—";
    [ObservableProperty] private string _mapsDisplay = "—";
    [ObservableProperty] private bool _puaEnabled;
    [ObservableProperty] private bool _cfaEnabled;
    [ObservableProperty] private string? _selectedExclusion;

    public DefenderViewModel(DefenderService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        StatusMessage = "Reading Defender status…";
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(RefreshAsync);
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    /// <summary>True when no Defender operation is in flight — gates the mutating
    /// commands so a user can't start a second Set-MpPreference while the first is
    /// still running (each spins its own runspace and the read-back verification
    /// could race). Mirrors the NotBusy convention used across the other VMs.</summary>
    public bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        OnPropertyChanged(nameof(NotBusy));
        RefreshCommand.NotifyCanExecuteChanged();
        TogglePuaCommand.NotifyCanExecuteChanged();
        ToggleCfaCommand.NotifyCanExecuteChanged();
        AddExclusionCommand.NotifyCanExecuteChanged();
        RemoveExclusionCommand.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) PropertyChanged -= OnVmPropertyChanged;
        base.Dispose(disposing);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var status = await _service.GetStatusAsync().ConfigureAwait(true);
            Apply(status);
            StatusMessage = !IsAvailable
                ? "Microsoft Defender is not available on this system."
                : IsTamperProtected
                    ? "Tamper Protection is ON — some changes may be ignored by Windows until you turn it off in Windows Security."
                    : "Defender status loaded.";
        }
        // GetStatusAsync runs PowerShell; a runspace-level fault (not just a script
        // RuntimeException the service catches) would otherwise escape this async
        // command unobserved. Surface it as a status message instead.
        catch (InvalidOperationException ex) { StatusMessage = $"Could not read Defender status: {ex.Message}"; }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = $"Could not read Defender status: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task TogglePuaAsync()
    {
        if (!Confirm($"{(PuaEnabled ? "Disable" : "Enable")} potentially-unwanted-app (PUA) protection?")) return;
        IsBusy = true;
        try
        {
            int target = PuaEnabled ? 0 : 1;
            var status = await _service.SetPuaProtectionAsync(target).ConfigureAwait(true);
            Apply(status);
            ReportVerified("PUA protection", status, status.PuaProtection == target);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Could not change PUA protection: {ex.Message}"; }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = $"Could not change PUA protection: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ToggleCfaAsync()
    {
        if (!Confirm($"{(CfaEnabled ? "Disable" : "Enable")} Controlled Folder Access (ransomware protection)?")) return;
        IsBusy = true;
        try
        {
            int target = CfaEnabled ? 0 : 1;
            var status = await _service.SetControlledFolderAccessAsync(target).ConfigureAwait(true);
            Apply(status);
            ReportVerified("Controlled Folder Access", status, status.ControlledFolderAccess == target);
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Could not change Controlled Folder Access: {ex.Message}"; }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = $"Could not change Controlled Folder Access: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task AddExclusionAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select a folder to exclude from scanning" };
        if (dialog.ShowDialog() != true) return;

        string path = dialog.FolderName;
        if (!IsValidExclusionPath(path))
        {
            StatusMessage = "That folder path is not valid.";
            return;
        }
        if (!Confirm($"Exclude \"{path}\" from Defender scanning?\n\nFiles in an excluded folder are not scanned for malware.")) return;

        IsBusy = true;
        try
        {
            var status = await _service.AddExclusionPathAsync(path).ConfigureAwait(true);
            Apply(status);
            ReportVerified("Exclusion", status, status.ExclusionPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Could not add the exclusion: {ex.Message}"; }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = $"Could not add the exclusion: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveExclusion))]
    private async Task RemoveExclusionAsync()
    {
        string? path = SelectedExclusion;
        if (path is null) return;
        if (!Confirm($"Stop excluding \"{path}\"?\n\nThe folder will be scanned for malware again.")) return;

        IsBusy = true;
        try
        {
            var status = await _service.RemoveExclusionPathAsync(path).ConfigureAwait(true);
            Apply(status);
            ReportVerified("Exclusion removal", status, !status.ExclusionPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
        }
        catch (InvalidOperationException ex) { StatusMessage = $"Could not remove the exclusion: {ex.Message}"; }
        catch (System.ComponentModel.Win32Exception ex) { StatusMessage = $"Could not remove the exclusion: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private bool HasSelectedExclusion => SelectedExclusion is not null;
    // Remove must be both busy-gated and have a selection.
    private bool CanRemoveExclusion => NotBusy && HasSelectedExclusion;
    partial void OnSelectedExclusionChanged(string? value) => RemoveExclusionCommand.NotifyCanExecuteChanged();

    /// <summary>A valid exclusion is a rooted, existing folder with no wildcards.</summary>
    internal static bool IsValidExclusionPath(string path)
        => !string.IsNullOrWhiteSpace(path)
           && Path.IsPathRooted(path)
           && !path.Contains('*') && !path.Contains('?')
           && path.Length <= 260
           && Directory.Exists(path);

    private void Apply(DefenderStatus status)
    {
        IsAvailable = status.Available;
        IsTamperProtected = status.IsTamperProtected;
        RealtimeDisplay = status.RealtimeDisplay;
        PuaDisplay = status.PuaDisplay;
        CfaDisplay = status.CfaDisplay;
        MapsDisplay = status.MapsDisplay;
        PuaEnabled = status.PuaProtection == 1;
        CfaEnabled = status.ControlledFolderAccess == 1;
        ExclusionPaths.ReplaceWith(status.ExclusionPaths);
        RemoveExclusionCommand.NotifyCanExecuteChanged();
    }

    private void ReportVerified(string what, DefenderStatus status, bool applied)
    {
        // A read-back only proves anything when the status is actually readable. When the
        // Set failed (needs admin / PowerShell host fault), the service returns the all-zeros
        // DefenderStatus.Unavailable — against which a disable-toggle (target 0) or an
        // exclusion removal (empty list) would FALSELY satisfy `applied`. Treat an
        // unavailable read-back as a failure, never a silent success.
        if (applied && status.Available)
        {
            StatusMessage = $"{what} updated.";
            Log.Information("Defender: {What} change applied", what);
        }
        else
        {
            StatusMessage = IsTamperProtected
                ? $"{what} was not changed — Tamper Protection blocked it. Turn it off in Windows Security first."
                : $"{what} was not changed — this needs administrator rights.";
        }
    }

    private static bool Confirm(string message)
        => DialogService.Instance.Confirm(message, "Defender — Confirm");
}
