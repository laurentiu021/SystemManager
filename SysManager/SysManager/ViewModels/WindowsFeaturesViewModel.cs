// SysManager · WindowsFeaturesViewModel — toggle Windows optional features
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
/// Windows Features tab — lists optional features, allows enable/disable toggle.
/// Requires administrator privileges for modifications.
/// </summary>
public partial class WindowsFeaturesViewModel : ViewModelBase
{
    private readonly WindowsFeaturesService _service;
    private CancellationTokenSource? _cts;

    public ObservableCollection<WindowsFeature> AllFeatures { get; } = new();
    public ObservableCollection<WindowsFeature> FilteredFeatures { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private int _featureCount;
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private string _summary = "Click Scan to list Windows optional features.";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private bool _pendingReboot;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public WindowsFeaturesViewModel(PowerShellRunner runner)
    {
        _service = new WindowsFeaturesService(runner);
        IsElevated = AdminHelper.IsElevated();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Querying Windows optional features…";
        AllFeatures.Clear();
        FilteredFeatures.Clear();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var list = await _service.ListFeaturesAsync(_cts.Token);
            foreach (var feature in list)
                AllFeatures.Add(feature);

            ApplyFilter();
            EnabledCount = AllFeatures.Count(f => f.IsEnabled);
            StatusMessage = $"Found {AllFeatures.Count} features ({EnabledCount} enabled).";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleFeatureAsync(WindowsFeature? feature)
    {
        if (feature == null) return;

        if (!IsElevated)
        {
            StatusMessage = "Administrator privileges required to modify features.";
            return;
        }

        var action = feature.IsEnabled ? "disable" : "enable";
        if (!DialogService.Instance.Confirm(
            $"Are you sure you want to {action} '{feature.DisplayName}'?\n\n" +
            "This may require a system reboot to take effect.",
            $"Confirm {action} feature"))
            return;

        IsBusy = true;
        ToggleFeatureCommand.NotifyCanExecuteChanged();
        feature.Status = feature.IsEnabled ? "Disabling…" : "Enabling…";
        StatusMessage = $"{(feature.IsEnabled ? "Disabling" : "Enabling")} {feature.DisplayName}…";
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            bool success;
            bool reboot;

            if (feature.IsEnabled)
            {
                (success, reboot) = await _service.DisableFeatureAsync(feature.Name, _cts.Token);
            }
            else
            {
                (success, reboot) = await _service.EnableFeatureAsync(feature.Name, _cts.Token);
            }

            if (success)
            {
                feature.IsEnabled = !feature.IsEnabled;
                feature.RequiresReboot = reboot;
                feature.Status = reboot ? "Reboot required" : "Done";
                EnabledCount = AllFeatures.Count(f => f.IsEnabled);

                if (reboot) PendingReboot = true;

                StatusMessage = $"{feature.DisplayName} {(feature.IsEnabled ? "enabled" : "disabled")}" +
                    (reboot ? " — reboot required." : ".");
                Log.Information("Feature {Feature} toggled to {State}, reboot={Reboot}",
                    feature.Name, feature.IsEnabled ? "Enabled" : "Disabled", reboot);
            }
            else
            {
                feature.Status = "Failed";
                StatusMessage = $"Failed to {action} {feature.DisplayName}. Check permissions.";
            }
        }
        catch (OperationCanceledException)
        {
            feature.Status = "Cancelled";
            StatusMessage = "Operation cancelled.";
        }
        catch (InvalidOperationException ex)
        {
            feature.Status = "Error";
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ToggleFeatureCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private bool CanToggle(WindowsFeature? _) => !IsBusy;

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cts?.Cancel(); _cts?.Dispose(); }
        base.Dispose(disposing);
    }

    private void ApplyFilter()
    {
        FilteredFeatures.Clear();
        IEnumerable<WindowsFeature> source = AllFeatures;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var f = FilterText.Trim();
            source = source.Where(feat =>
                feat.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                feat.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                feat.Category.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var feat in source)
            FilteredFeatures.Add(feat);

        FeatureCount = FilteredFeatures.Count;
        Summary = $"{FeatureCount} features{(AllFeatures.Count != FeatureCount ? $" (of {AllFeatures.Count} total)" : "")}";
    }
}
