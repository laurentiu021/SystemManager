// SysManager · FileLockViewModel
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
/// ViewModel for the File Lock Detector tab. Takes a file/folder path and reports the
/// processes using it via the Restart Manager, with an option to terminate a locker
/// (after confirmation). Detection works as a standard user; killing a locker owned by
/// SYSTEM or another user needs elevation, surfaced rather than thrown.
/// </summary>
public sealed partial class FileLockViewModel : ViewModelBase
{
    private readonly FileLockService _service;

    public BulkObservableCollection<FileLocker> Lockers { get; } = new();

    [ObservableProperty] private string _path = "";
    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private FileLocker? _selectedLocker;

    public FileLockViewModel(FileLockService service)
    {
        _service = service;
        StatusMessage = "Enter a file or folder path, then scan to see what's using it.";
        PropertyChanged += OnVmPropertyChanged;
    }

    private bool CanScan => !IsBusy && !string.IsNullOrWhiteSpace(Path);

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy))
        {
            ScanCommand.NotifyCanExecuteChanged();
            KillSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnPathChanged(string value) => ScanCommand.NotifyCanExecuteChanged();
    partial void OnSelectedLockerChanged(FileLocker? value) => KillSelectedCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a file to check",
            CheckFileExists = false,
            Filter = "All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
            Path = dialog.FileName;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Scanning…";
        try
        {
            string target = Path.Trim().Trim('"');
            var lockers = await Task.Run(() => _service.FindLockers(target)).ConfigureAwait(true);
            Lockers.ReplaceWith(lockers);
            HasScanned = true;
            StatusMessage = lockers.Count == 0
                ? "No process is currently using that path."
                : $"{lockers.Count} process(es) are using that path.";
        }
        catch (ArgumentException)
        {
            StatusMessage = "Please enter a valid file or folder path.";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    private bool CanKill => !IsBusy && SelectedLocker is not null;

    [RelayCommand(CanExecute = nameof(CanKill))]
    private void KillSelected()
    {
        var locker = SelectedLocker;
        if (locker is null) return;

        if (locker.IsCritical)
        {
            DialogService.Instance.Confirm(
                $"\"{locker.ProcessName}\" is a critical system process and cannot be safely ended from here.",
                "Cannot End Process");
            return;
        }

        if (!DialogService.Instance.Confirm(
            $"End \"{locker.Display}\"?\n\nUnsaved work in that process will be lost. This force-terminates the process to release the file.",
            "End Process — Confirm")) return;

        bool ok = _service.KillProcess(locker.ProcessId);
        if (ok)
        {
            Log.Information("User ended locking process {Pid} ({Name})", locker.ProcessId, locker.ProcessName);
            StatusMessage = $"Ended {locker.Display}. Re-scanning…";
            ScanCommand.Execute(null);
        }
        else
        {
            StatusMessage = $"Couldn't end {locker.Display} — it may need administrator rights, or it already exited.";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            PropertyChanged -= OnVmPropertyChanged;
        base.Dispose(disposing);
    }
}
