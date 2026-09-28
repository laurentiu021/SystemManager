// SysManager · FileLockViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
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
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => ScanCommand;

    private readonly IFileLockService _service;

    public BulkObservableCollection<FileLocker> Lockers { get; } = new();

    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private bool _hasScanned;

    /// <summary>How many lockers the last scan found. Drives the export button's enabled state.</summary>
    /// <remarks>
    /// A count rather than a bool, and assigned where <c>Lockers</c> is replaced, because
    /// <c>CanExecute</c> cannot observe a collection: <c>BulkObservableCollection</c> raises
    /// <c>CollectionChanged</c>, which no generated command listens to. Deriving the button's state from a
    /// property the scan already has to set keeps the two from disagreeing.
    /// </remarks>
    [ObservableProperty] private int _lockerCount;

    /// <summary>Whether the last scan found anything worth exporting.</summary>
    public bool HasLockers => LockerCount > 0;

    [ObservableProperty] private FileLocker? _selectedLocker;

    public FileLockViewModel(IFileLockService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        StatusMessage = "Enter a file or folder path, then scan to see what's using it.";
        PropertyChanged += OnVmPropertyChanged;
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
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
            var scan = await Task.Run(() => _service.FindLockers(target)).ConfigureAwait(true);
            HasScanned = true;
            if (scan is null)
            {
                // Not "no process": that is the answer someone who cannot delete a file acts on (#2502). The list is
                // cleared rather than kept, because what it showed may belong to a different path.
                ShowLockers([]);
                StatusMessage = "The check could not be completed — Windows did not answer. Try again in a moment.";
                return;
            }

            ShowLockers(scan.Lockers);
            StatusMessage = DescribeScan(scan);
        }
        catch (FileNotFoundException)
        {
            ShowLockers([]);
            StatusMessage = "No file or folder exists at that path. Check the spelling, or use Browse to pick the file.";
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

    private void ShowLockers(IReadOnlyList<FileLocker> lockers)
    {
        Lockers.ReplaceWith(lockers);
        LockerCount = lockers.Count;
    }

    /// <summary>
    /// The status line for a check that completed. Pure, so every case is testable without Restart Manager.
    /// </summary>
    /// <remarks>
    /// A folder is checked through the files inside it, so the sentence says so. "No process is using any of the
    /// files" is a claim about those files, not about the folder, and it is only as complete as the list checked,
    /// which is why a folder too large to check whole says how much was covered.
    /// </remarks>
    internal static string DescribeScan(FileLockScan scan)
    {
        var count = scan.Lockers.Count;
        if (!scan.IsFolder)
            return count == 0
                ? "No process is currently using that file."
                : $"{count} process(es) are using that file.";

        if (scan.FilesChecked == 0)
            return "SysManager found no files it could check in that folder.";

        var files = scan.FilesChecked.ToString("N0", CultureInfo.CurrentCulture);
        var partial = scan.CheckedOnlyPart ? $" The folder holds more files; the first {files} were checked." : "";
        return count == 0
            ? $"No process is using any of the {files} files in that folder.{partial}"
            : $"{count} process(es) are using files in that folder.{partial}";
    }

    private bool CanKill => !IsBusy && SelectedLocker is not null;

    [RelayCommand(CanExecute = nameof(CanKill))]
    private void KillSelected()
    {
        var locker = SelectedLocker;
        if (locker is null) return;

        if (locker.IsCritical)
        {
            // Inform, not Confirm. Nothing is being decided — the process will not be ended either way —
            // but this was a Yes/No dialog whose answer was discarded, so the user chose between two
            // buttons that did the same thing.
            DialogService.Instance.Inform(
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

    /// <summary>
    /// Writes the list of processes holding the scanned path to a CSV the user picks a location for.
    /// </summary>
    /// <remarks>
    /// "What is using this file" is usually asked because something cannot be deleted or ejected, and the answer often has to be passed on — including the critical flag, which is the one row nobody should be told to end.
    /// <para>The file goes only where the dialog is pointed — nothing is written to a default location and
    /// nothing leaves the machine.</para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasLockers))]
    private async Task ExportCsvAsync()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"SysManager-FileLocks-{DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)}.csv",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var csv = FileLockService.ToCsv(Lockers);
            await File.WriteAllTextAsync(dlg.FileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            // System.IO.Path in full: this view-model has its own `Path` property — the file being
            // scanned — which shadows the static class, so the unqualified call binds to a string.
            var saved = System.IO.Path.GetFileName(dlg.FileName);
            StatusMessage = $"Exported {Lockers.Count} process(es) to {saved}.";
            ToastService.Instance.Show("Lock list exported", saved);
        }
        catch (IOException ex) { StatusMessage = $"Export failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Export failed (access denied): {ex.Message}"; }
    }

    partial void OnLockerCountChanged(int value) => ExportCsvCommand.NotifyCanExecuteChanged();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            PropertyChanged -= OnVmPropertyChanged;
        base.Dispose(disposing);
    }
}
