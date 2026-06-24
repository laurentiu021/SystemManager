// SysManager · RestorePointsViewModel — list, create, and restore System Restore points
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
/// ViewModel for the Restore Points tab. Lists existing System Restore points,
/// creates new ones, and restores from a selected point. Creating and restoring
/// require administrator rights; restoring reboots the machine and is gated behind
/// an explicit confirmation dialog.
/// </summary>
public sealed partial class RestorePointsViewModel : ViewModelBase
{
    private readonly RestorePointService _service;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<RestorePoint> RestorePoints { get; } = new();

    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private bool _hasPoints;
    [ObservableProperty] private string _newDescription = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RestorePoint? _selectedPoint;

    public bool HasSelection => SelectedPoint is not null;

    public RestorePointsViewModel(RestorePointService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        StatusMessage = "Loading restore points…";
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(RefreshAsync);
    }

    private bool NotBusy => !IsBusy;
    private bool CanCreate => !IsBusy && IsElevated;
    private bool CanRestore => !IsBusy && IsElevated && HasSelection;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy) or nameof(IsElevated) or nameof(HasSelection))
        {
            RefreshCommand.NotifyCanExecuteChanged();
            CreateCommand.NotifyCanExecuteChanged();
            RestoreCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Reading System Restore points…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var points = await _service.ListAsync(_cts.Token).ConfigureAwait(true);
            RestorePoints.ReplaceWith(points);
            HasPoints = points.Count > 0;
            StatusMessage = points.Count == 0
                ? "No restore points found. System Restore may be turned off for this PC."
                : $"{points.Count} restore point{(points.Count == 1 ? "" : "s")}.";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var description = string.IsNullOrWhiteSpace(NewDescription)
            ? $"SysManager — {DateTime.Now:yyyy-MM-dd HH:mm}"
            : NewDescription.Trim();

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Creating restore point…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var ok = await _service.CreateAsync(description, _cts.Token).ConfigureAwait(true);
            if (ok)
            {
                NewDescription = "";
                StatusMessage = "Restore point created.";
                ToastService.Instance.Show("Restore point created", description);
                var points = await _service.ListAsync(_cts.Token).ConfigureAwait(true);
                RestorePoints.ReplaceWith(points);
                HasPoints = points.Count > 0;
            }
            else
            {
                StatusMessage = "Could not create a restore point. Windows allows only one every 24 hours, " +
                    "and System Restore must be enabled for the system drive.";
            }
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        if (SelectedPoint is not { } point) return;

        if (!DialogService.Instance.Confirm(
                $"Restore this PC to:\n\n#{point.SequenceNumber} — {point.Description}\n{point.CreatedDisplay}\n\n" +
                "Windows will RESTART to apply the restore. Open files should be saved and " +
                "other apps closed first. Your personal files are not affected, but programs and " +
                "drivers installed after this point will be removed.\n\nContinue?",
                "Restore System — this will restart Windows"))
        {
            StatusMessage = "Restore cancelled.";
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Starting system restore — Windows will restart…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var ok = await _service.RestoreAsync(point.SequenceNumber, _cts.Token).ConfigureAwait(true);
            // On success the machine reboots, so this line is usually never reached.
            StatusMessage = ok
                ? "Restore initiated — the system will restart."
                : "Could not start the restore. Make sure System Restore is enabled and try again.";
            Log.Information("RestorePoint: restore to #{Seq} requested (ok={Ok})", point.SequenceNumber, ok);
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
