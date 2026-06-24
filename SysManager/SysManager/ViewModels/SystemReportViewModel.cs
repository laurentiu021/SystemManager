// SysManager · SystemReportViewModel — generate and export a full system report
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// System Report tab — gathers a full hardware/OS/network snapshot and exports it
/// as plain text, HTML, or JSON. Read-only: it never modifies the system, and the
/// report is written only to a file the user picks. Nothing leaves the machine.
/// </summary>
public sealed partial class SystemReportViewModel : ViewModelBase
{
    private readonly SystemReportService _service;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _reportText = "";
    [ObservableProperty] private bool _hasReport;

    public SystemReportViewModel(SystemReportService service)
    {
        _service = service;
        StatusMessage = "Click Generate to build a full system report.";
        // Generate / the three exports / Copy all run a fresh gather; disable them while one
        // runs so a second click can't dispose the cancellation source the first is awaiting.
        PropertyChanged += OnVmPropertyChanged;
    }

    private bool NotBusy => !IsBusy;
    private bool CanExport => !IsBusy && HasReport;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy) or nameof(HasReport))
        {
            GenerateCommand.NotifyCanExecuteChanged();
            ExportTextCommand.NotifyCanExecuteChanged();
            ExportHtmlCommand.NotifyCanExecuteChanged();
            ExportJsonCommand.NotifyCanExecuteChanged();
            CopyCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task GenerateAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Gathering system information…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            ReportText = await _service.GenerateReportAsync(_cts.Token);
            HasReport = true;
            StatusMessage = "Report ready. Export it as text, HTML, or JSON.";
            ToastService.Instance.Show("System report ready", "Export it as text, HTML, or JSON.");
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        catch (InvalidOperationException ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportTextAsync() => ExportAsync("txt", "Text file (*.txt)|*.txt", _service.GenerateReportAsync);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportHtmlAsync() => ExportAsync("html", "HTML file (*.html)|*.html", _service.GenerateHtmlAsync);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportJsonAsync() => ExportAsync("json", "JSON file (*.json)|*.json", _service.GenerateJsonAsync);

    private async Task ExportAsync(string extension, string filter, Func<CancellationToken, Task<string>> generate)
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"SysManager-Report-{DateTime.Now:yyyy-MM-dd-HHmmss}.{extension}",
            Filter = filter + "|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        StatusMessage = "Exporting…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var content = await generate(_cts.Token);
            await File.WriteAllTextAsync(dlg.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), _cts.Token);
            StatusMessage = $"Saved {Path.GetFileName(dlg.FileName)}.";
            ToastService.Instance.Show("Report exported", Path.GetFileName(dlg.FileName));
        }
        catch (OperationCanceledException) { StatusMessage = "Export cancelled."; }
        catch (IOException ex) { StatusMessage = $"Export failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Export failed (access denied): {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Copy()
    {
        try
        {
            Clipboard.SetText(ReportText);
            StatusMessage = "Report copied to clipboard.";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Debug("Clipboard locked: {Error}", ex.Message);
            StatusMessage = "Couldn't copy: the clipboard is in use by another application.";
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

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
