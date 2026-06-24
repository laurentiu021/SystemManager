// SysManager · WindowsFeaturesViewModel — toggle Windows optional features
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
public sealed partial class WindowsFeaturesViewModel : ViewModelBase
{
    private readonly WindowsFeaturesService _service;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _toggleCts;

    public BulkObservableCollection<WindowsFeature> AllFeatures { get; } = new();
    public BulkObservableCollection<WindowsFeature> FilteredFeatures { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private int _featureCount;
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private string _summary = "Click Scan to list Windows optional features.";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private bool _pendingReboot;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public WindowsFeaturesViewModel(WindowsFeaturesService service)
    {
        _service = service;
        // Scan and ToggleFeature both drive the shared WindowsFeaturesService PowerShell
        // runner (each subscribes its own LineReceived handler). Running them concurrently
        // would cross-contaminate the captured output (the SFC/DISM bug class). Re-evaluate
        // both commands' CanExecute when IsBusy flips so they are mutually exclusive.
        PropertyChanged += OnVmPropertyChanged;
        IsElevated = AdminHelper.IsElevated();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        ScanCommand.NotifyCanExecuteChanged();
        ToggleFeatureCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Querying Windows optional features…";
        FilteredFeatures.Clear();
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        try
        {
            var list = await _service.ListFeaturesAsync(_scanCts.Token);
            AllFeatures.ReplaceWith(list);

            ApplyFilter();
            EnabledCount = AllFeatures.Count(f => f.IsEnabled);
            StatusMessage = $"Found {AllFeatures.Count} features ({EnabledCount} enabled).";
            ToastService.Instance.Show("Windows Features scan complete", $"Found {AllFeatures.Count} features");
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
        if (feature is null) return;

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
        feature.Status = feature.IsEnabled ? "Disabling…" : "Enabling…";
        StatusMessage = $"{(feature.IsEnabled ? "Disabling" : "Enabling")} {feature.DisplayName}…";
        _toggleCts?.Cancel();
        _toggleCts?.Dispose();
        _toggleCts = new CancellationTokenSource();

        try
        {
            var (success, reboot) = feature.IsEnabled
                ? await _service.DisableFeatureAsync(feature.Name, _toggleCts.Token)
                : await _service.EnableFeatureAsync(feature.Name, _toggleCts.Token);

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
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _scanCts?.Cancel();
        _toggleCts?.Cancel();
    }

    private bool CanToggle(WindowsFeature? _) => !IsBusy;

    /// <summary>Gate for Scan so it can't run while a feature toggle is in flight (and vice
    /// versa) — both share one PowerShell runner whose LineReceived output would otherwise
    /// be captured by both operations at once.</summary>
    private bool NotBusy => !IsBusy;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            _scanCts?.Cancel(); _scanCts?.Dispose();
            _toggleCts?.Cancel(); _toggleCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ApplyFilter()
    {
        IEnumerable<WindowsFeature> source = AllFeatures;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var f = FilterText.Trim();
            source = source.Where(feat =>
                feat.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                feat.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                feat.Category.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        FilteredFeatures.ReplaceWith(source);
        FeatureCount = FilteredFeatures.Count;
        Summary = $"{FeatureCount} features{(AllFeatures.Count != FeatureCount ? $" (of {AllFeatures.Count} total)" : "")}";
    }
}
