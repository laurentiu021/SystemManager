// SysManager · CleanupViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
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
    [ObservableProperty] private string _sfcVerdict = "";
    [ObservableProperty] private string _sfcVerdictColorHex = "#9AA0A6";
    [ObservableProperty] private string _dismStatus = "Idle";
    [ObservableProperty] private string _dismVerdict = "";
    [ObservableProperty] private string _dismVerdictColorHex = "#9AA0A6";

    // Pre-scan info so the tab doesn't look empty on first load
    [ObservableProperty] private string _tempSizeLabel = "Scanning…";
    [ObservableProperty] private string _recycleBinLabel = "Scanning…";

    /// <summary>True whenever any background task is running — for a small badge.</summary>
    public bool IsAnyRunning => IsTempRunning || IsBinRunning || IsSfcRunning || IsDismRunning;

    public CleanupViewModel(PowerShellRunner runner)
    {
        _runner = runner;
        _runner.LineReceived += l => Console.Append(l);
        _runner.ProgressChanged += p => Progress = p;
        IsElevated = AdminHelper.IsElevated();

        // Auto-scan temp and recycle bin sizes so the tab isn't empty on load
        _ = PreScanAsync();
    }

    [RelayCommand]
    private async Task RescanAsync() => await PreScanAsync();

    private async Task PreScanAsync()
    {
        try
        {
            var (tempLabel, binLabel) = await Task.Run(() =>
            {
                // Measure temp folders
                long tempBytes = 0;
                var tempPaths = new[] { Environment.GetEnvironmentVariable("TEMP") ?? "", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };
                foreach (var p in tempPaths)
                {
                    if (string.IsNullOrEmpty(p) || !System.IO.Directory.Exists(p)) continue;
                    try
                    {
                        foreach (var f in System.IO.Directory.EnumerateFiles(p, "*", System.IO.SearchOption.AllDirectories))
                        {
                            try { tempBytes += new System.IO.FileInfo(f).Length; } catch { }
                        }
                    }
                    catch { }
                }
                var tLabel = tempBytes > 0 ? $"{tempBytes / 1024.0 / 1024.0:F1} MB can be freed" : "Empty";

                // Measure recycle bin (rough estimate via shell folder)
                string bLabel;
                try
                {
                    long binBytes = 0;
                    var recyclePath = @"C:\$Recycle.Bin";
                    if (System.IO.Directory.Exists(recyclePath))
                    {
                        foreach (var f in System.IO.Directory.EnumerateFiles(recyclePath, "*", System.IO.SearchOption.AllDirectories))
                        {
                            try { binBytes += new System.IO.FileInfo(f).Length; } catch { }
                        }
                    }
                    bLabel = binBytes > 0 ? $"{binBytes / 1024.0 / 1024.0:F1} MB in Recycle Bin" : "Empty";
                }
                catch { bLabel = "Unable to scan"; }

                return (tLabel, bLabel);
            });

            // Update on the calling (UI) thread so PropertyChanged fires correctly
            TempSizeLabel = tempLabel;
            RecycleBinLabel = binLabel;
        }
        catch { /* non-fatal */ }
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
        _tempCts?.Dispose();
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
            Log.Information("Temp cleanup completed");
            await PreScanAsync();
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
        _binCts?.Dispose();
        _binCts = new CancellationTokenSource();
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                Clear-RecycleBin -Force -ErrorAction SilentlyContinue
                'Recycle Bin cleared'
            ", cancellationToken: _binCts.Token);
            StatusMessage = "Done";
            await PreScanAsync();
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
            var result = System.Windows.MessageBox.Show(
                "SFC requires admin privileges. Restart the application with elevated privileges?",
                "Admin Required",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes)
            {
                StatusMessage = "SFC cancelled — admin privileges required.";
                return;
            }
            if (AdminHelper.RelaunchAsAdmin()) System.Windows.Application.Current?.Shutdown();
            return;
        }

        IsSfcRunning = true;
        SfcStatus = "Running — can take 5–15 minutes";
        SfcVerdict = "";
        SfcVerdictColorHex = "#9AA0A6";
        StatusMessage = "SFC running in background. You can keep using the app.";
        _sfcCts?.Dispose();
        _sfcCts = new CancellationTokenSource();
        var captured = new System.Collections.Generic.List<string>();
        void Collect(PowerShellLine l) { if (l.Kind == OutputKind.Output) captured.Add(l.Text); }
        _runner.LineReceived += Collect;
        try
        {
            var exit = await _runner.RunProcessAsync("sfc.exe", "/scannow", _sfcCts.Token,
                System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage));
            var (verdict, color) = ParseSfcResult(captured, exit);
            SfcVerdict = verdict;
            SfcVerdictColorHex = color;
            SfcStatus = exit == 0 ? "Completed" : $"Finished (exit {exit})";
            StatusMessage = verdict;
        }
        catch (OperationCanceledException) { SfcStatus = "Cancelled."; SfcVerdict = "Scan was cancelled."; SfcVerdictColorHex = "#9AA0A6"; StatusMessage = SfcStatus; }
        catch (Exception ex) { SfcStatus = $"Error: {ex.Message}"; SfcVerdict = ex.Message; SfcVerdictColorHex = "#EF4444"; StatusMessage = SfcStatus; }
        finally { _runner.LineReceived -= Collect; IsSfcRunning = false; }
    }

    /// <summary>
    /// Parses the captured SFC output lines to produce a human-readable verdict
    /// with an appropriate color. SFC writes its results in the OEM code page,
    /// so we match on key phrases that appear in all locales.
    /// </summary>
    internal static (string Verdict, string ColorHex) ParseSfcResult(IReadOnlyList<string> lines, int exitCode)
    {
        var all = string.Join(" ", lines);

        // "did not find any integrity violations"
        if (all.Contains("did not find any integrity violations", StringComparison.OrdinalIgnoreCase))
            return ("No integrity violations found — your system files are healthy.", "#22C55E");

        // "found corrupt files and successfully repaired them"
        if (all.Contains("successfully repaired", StringComparison.OrdinalIgnoreCase))
            return ("Corrupted files were found and successfully repaired.", "#F59E0B");

        // "found corrupt files but was unable to fix some of them"
        if (all.Contains("unable to fix", StringComparison.OrdinalIgnoreCase))
            return ("Corrupted files found but SFC could not repair them. Try running DISM /RestoreHealth first, then SFC again.", "#EF4444");

        // "could not perform the requested operation"
        if (all.Contains("could not perform", StringComparison.OrdinalIgnoreCase))
            return ("SFC could not run. Try rebooting into Safe Mode or running DISM first.", "#EF4444");

        // Fallback based on exit code
        return exitCode == 0
            ? ("Scan completed successfully.", "#22C55E")
            : ($"Scan finished with exit code {exitCode}. Check the console output for details.", "#F59E0B");
    }

    [RelayCommand]
    private async Task RunDismAsync()
    {
        if (IsDismRunning) return;
        if (!AdminHelper.IsElevated())
        {
            var result = System.Windows.MessageBox.Show(
                "DISM requires admin privileges. Restart the application with elevated privileges?",
                "Admin Required",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes)
            {
                StatusMessage = "DISM cancelled — admin privileges required.";
                return;
            }
            if (AdminHelper.RelaunchAsAdmin()) System.Windows.Application.Current?.Shutdown();
            return;
        }

        IsDismRunning = true;
        DismStatus = "Running — can take 10–30 minutes";
        DismVerdict = "";
        DismVerdictColorHex = "#9AA0A6";
        StatusMessage = "DISM running in background. You can keep using the app.";
        _dismCts?.Dispose();
        _dismCts = new CancellationTokenSource();
        var captured = new System.Collections.Generic.List<string>();
        void Collect(PowerShellLine l) { if (l.Kind == OutputKind.Output) captured.Add(l.Text); }
        _runner.LineReceived += Collect;
        try
        {
            var exit = await _runner.RunProcessAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", _dismCts.Token,
                System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage));
            var (verdict, color) = ParseDismResult(captured, exit);
            DismVerdict = verdict;
            DismVerdictColorHex = color;
            DismStatus = exit == 0 ? "Completed" : $"Finished (exit {exit})";
            StatusMessage = verdict;
        }
        catch (OperationCanceledException) { DismStatus = "Cancelled."; DismVerdict = "Repair was cancelled."; DismVerdictColorHex = "#9AA0A6"; StatusMessage = DismStatus; }
        catch (Exception ex) { DismStatus = $"Error: {ex.Message}"; DismVerdict = ex.Message; DismVerdictColorHex = "#EF4444"; StatusMessage = DismStatus; }
        finally { _runner.LineReceived -= Collect; IsDismRunning = false; }
    }

    /// <summary>
    /// Parses DISM RestoreHealth output into a verdict with color.
    /// </summary>
    internal static (string Verdict, string ColorHex) ParseDismResult(IReadOnlyList<string> lines, int exitCode)
    {
        var all = string.Join(" ", lines);

        if (all.Contains("The restore operation completed successfully", StringComparison.OrdinalIgnoreCase))
            return ("Component store is healthy — no repairs needed.", "#22C55E");

        if (all.Contains("The component store corruption was repaired", StringComparison.OrdinalIgnoreCase))
            return ("Component store was corrupted and has been repaired. Run SFC /scannow next.", "#F59E0B");

        if (all.Contains("source files could not be found", StringComparison.OrdinalIgnoreCase))
            return ("DISM could not find source files for repair. Try connecting to the internet or using a Windows ISO.", "#EF4444");

        return exitCode == 0
            ? ("Repair completed successfully.", "#22C55E")
            : ($"DISM finished with exit code {exitCode}. Check the console output for details.", "#F59E0B");
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
