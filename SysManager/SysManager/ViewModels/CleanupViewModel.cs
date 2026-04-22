// SysManager · CleanupViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.ViewModels;

public partial class CleanupViewModel : ViewModelBase
{
    private readonly PowerShellRunner _runner;

    private CancellationTokenSource? _tempCts;
    private CancellationTokenSource? _binCts;
    private CancellationTokenSource? _sfcCts;
    private CancellationTokenSource? _dismCts;

    public ConsoleViewModel Console { get; } = new();

    [ObservableProperty] private bool _isElevated;

    // Per-task running flags so buttons stay independent and the main thread
    // doesn't block a user navigating away while SFC grinds for 10 minutes.
    [ObservableProperty] private bool _isTempRunning;
    [ObservableProperty] private bool _isBinRunning;
    [ObservableProperty] private bool _isSfcRunning;
    [ObservableProperty] private bool _isDismRunning;

    [ObservableProperty] private string _sfcStatus = "Idle";
    [ObservableProperty] private string _dismStatus = "Idle";

    /// <summary>True whenever any background task is running — for a small badge.</summary>
    public bool IsAnyRunning => IsTempRunning || IsBinRunning || IsSfcRunning || IsDismRunning;

    public CleanupViewModel(PowerShellRunner runner)
    {
        _runner = runner;
        _runner.LineReceived += l => Console.Append(l);
        _runner.ProgressChanged += p => Progress = p;
        IsElevated = AdminHelper.IsElevated();
    }

    partial void OnIsTempRunningChanged(bool value) => OnPropertyChanged(nameof(IsAnyRunning));
    partial void OnIsBinRunningChanged(bool value) => OnPropertyChanged(nameof(IsAnyRunning));
    partial void OnIsSfcRunningChanged(bool value) => OnPropertyChanged(nameof(IsAnyRunning));
    partial void OnIsDismRunningChanged(bool value) => OnPropertyChanged(nameof(IsAnyRunning));

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand]
    private async Task CleanTempAsync()
    {
        if (IsTempRunning) return;
        IsTempRunning = true;
        StatusMessage = "Cleaning temp folders...";
        _tempCts = new CancellationTokenSource();
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                $paths = @($env:TEMP, ""$env:SystemRoot\Temp"")
                $totalBytes = 0
                foreach ($p in $paths) {
                    if (Test-Path $p) {
                        Get-ChildItem -Path $p -Recurse -Force -ErrorAction SilentlyContinue |
                            ForEach-Object {
                                try { $totalBytes += $_.Length } catch {}
                                try { Remove-Item $_.FullName -Force -Recurse -ErrorAction SilentlyContinue } catch {}
                            }
                    }
                }
                ""Freed approximately $([Math]::Round($totalBytes/1MB,1)) MB""
            ", cancellationToken: _tempCts.Token);
            StatusMessage = "Temp cleanup done";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsTempRunning = false; }
    }

    [RelayCommand]
    private async Task EmptyRecycleBinAsync()
    {
        if (IsBinRunning) return;
        IsBinRunning = true;
        StatusMessage = "Emptying Recycle Bin...";
        _binCts = new CancellationTokenSource();
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                Clear-RecycleBin -Force -ErrorAction SilentlyContinue
                'Recycle Bin cleared'
            ", cancellationToken: _binCts.Token);
            StatusMessage = "Done";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBinRunning = false; }
    }

    [RelayCommand]
    private async Task RunSfcAsync()
    {
        if (IsSfcRunning) return;
        if (!AdminHelper.IsElevated())
        {
            StatusMessage = "SFC requires admin";
            if (AdminHelper.RelaunchAsAdmin()) System.Windows.Application.Current?.Shutdown();
            return;
        }

        IsSfcRunning = true;
        SfcStatus = "Running — can take 5–15 minutes";
        StatusMessage = "SFC running in background. You can keep using the app.";
        _sfcCts = new CancellationTokenSource();
        try
        {
            var exit = await _runner.RunProcessAsync("sfc.exe", "/scannow", _sfcCts.Token);
            SfcStatus = exit == 0 ? "Completed — no integrity issues found (or all fixed)." : $"Finished with exit {exit}.";
            StatusMessage = SfcStatus;
        }
        catch (OperationCanceledException) { SfcStatus = "Cancelled."; StatusMessage = SfcStatus; }
        catch (Exception ex) { SfcStatus = $"Error: {ex.Message}"; StatusMessage = SfcStatus; }
        finally { IsSfcRunning = false; }
    }

    [RelayCommand]
    private async Task RunDismAsync()
    {
        if (IsDismRunning) return;
        if (!AdminHelper.IsElevated())
        {
            StatusMessage = "DISM requires admin";
            if (AdminHelper.RelaunchAsAdmin()) System.Windows.Application.Current?.Shutdown();
            return;
        }

        IsDismRunning = true;
        DismStatus = "Running — can take 10–30 minutes";
        StatusMessage = "DISM running in background. You can keep using the app.";
        _dismCts = new CancellationTokenSource();
        try
        {
            var exit = await _runner.RunProcessAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", _dismCts.Token);
            DismStatus = exit == 0 ? "Completed." : $"Finished with exit {exit}.";
            StatusMessage = DismStatus;
        }
        catch (OperationCanceledException) { DismStatus = "Cancelled."; StatusMessage = DismStatus; }
        catch (Exception ex) { DismStatus = $"Error: {ex.Message}"; StatusMessage = DismStatus; }
        finally { IsDismRunning = false; }
    }

    [RelayCommand]
    private void Cancel()
    {
        _tempCts?.Cancel();
        _binCts?.Cancel();
        _sfcCts?.Cancel();
        _dismCts?.Cancel();
    }
}
