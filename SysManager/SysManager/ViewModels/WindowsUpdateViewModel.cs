// SysManager · WindowsUpdateViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.ViewModels;

public partial class WindowsUpdateViewModel : ViewModelBase
{
    private readonly PowerShellRunner _runner;
    private CancellationTokenSource? _cts;

    public ConsoleViewModel Console { get; } = new();

    [ObservableProperty] private bool _moduleAvailable;
    [ObservableProperty] private string _moduleStatus = "Checking PSWindowsUpdate module...";
    [ObservableProperty] private bool _isElevated;

    public WindowsUpdateViewModel(PowerShellRunner runner)
    {
        _runner = runner;
        _runner.LineReceived += l => Console.Append(l);
        _runner.ProgressChanged += p => Progress = p;
        IsElevated = AdminHelper.IsElevated();

        // Auto-check the module the first time the tab is shown.
        _ = AutoCheckOnStartAsync();
    }

    /// <summary>
    /// Fires once on construction so the user sees the module status (and
    /// the "Install PSWindowsUpdate" banner if it's missing) without having
    /// to click anything.
    /// </summary>
    private async Task AutoCheckOnStartAsync()
    {
        try
        {
            // Give the UI a beat to bind before we start writing to it.
            await Task.Delay(250);
            await CheckModuleAsync();
        }
        catch
        {
            // Swallow — manual "Check module" still works.
        }
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current.Shutdown();
    }

    [RelayCommand]
    private async Task CheckModuleAsync()
    {
        IsBusy = true;
        try
        {
            var found = false;
            void Listen(Models.PowerShellLine l)
            {
                if (l.Kind == Models.OutputKind.Output && l.Text.Contains("AVAILABLE")) found = true;
            }
            _runner.LineReceived += Listen;
            try
            {
                await _runner.RunScriptViaPwshAsync(
                    "if (Get-Module -ListAvailable -Name PSWindowsUpdate) { 'AVAILABLE' } else { 'MISSING' }");
            }
            finally { _runner.LineReceived -= Listen; }
            ModuleAvailable = found;
            ModuleStatus = ModuleAvailable ? "PSWindowsUpdate is available." : "PSWindowsUpdate not installed — click Install Module.";
        }
        catch (Exception ex) { ModuleStatus = $"Check failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallModuleAsync()
    {
        if (!AdminHelper.IsElevated())
        {
            StatusMessage = "Requesting admin rights...";
            if (AdminHelper.RelaunchAsAdmin()) System.Windows.Application.Current.Shutdown();
            return;
        }
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Installing PSWindowsUpdate...";
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
                if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
                    Install-PackageProvider -Name NuGet -Force -Scope CurrentUser | Out-Null
                }
                Install-Module -Name PSWindowsUpdate -Force -Scope CurrentUser -AllowClobber
                Import-Module PSWindowsUpdate
                'INSTALLED'
            ");
            await CheckModuleAsync();
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private async Task ListUpdatesAsync()
    {
        IsBusy = true; IsProgressIndeterminate = true;
        StatusMessage = "Listing available Windows Updates...";
        _cts = new CancellationTokenSource();
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                Import-Module PSWindowsUpdate -ErrorAction Stop
                Write-Host '=== Standard updates (security, cumulative, drivers, defender) ==='
                Get-WindowsUpdate -MicrosoftUpdate |
                    Select-Object KB, Size, Title |
                    Format-Table -AutoSize -Wrap | Out-String -Width 250
                Write-Host ''
                Write-Host '=== Hidden updates (previously hidden by user) ==='
                try {
                    Get-WindowsUpdate -MicrosoftUpdate -IsHidden |
                        Select-Object KB, Size, Title |
                        Format-Table -AutoSize -Wrap | Out-String -Width 250
                } catch { 'None or not accessible' }
            ", cancellationToken: _cts.Token);
            StatusMessage = "Scan complete";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private async Task ListFeatureUpdatesAsync()
    {
        IsBusy = true; IsProgressIndeterminate = true;
        StatusMessage = "Checking for feature / version upgrades...";
        _cts = new CancellationTokenSource();
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                Import-Module PSWindowsUpdate -ErrorAction Stop
                Write-Host '=== Feature / version upgrades (e.g. Windows 11 24H2 -> 25H1) ==='
                $f = Get-WindowsUpdate -MicrosoftUpdate -UpdateType Software -Category 'Upgrades' -ErrorAction SilentlyContinue
                if (-not $f -or $f.Count -eq 0) { 'No feature upgrades available at this time.' }
                else { $f | Select-Object KB, Size, Title | Format-Table -AutoSize -Wrap | Out-String -Width 250 }
            ", cancellationToken: _cts.Token);
            StatusMessage = "Done";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private async Task ShowHistoryAsync()
    {
        IsBusy = true; IsProgressIndeterminate = true;
        StatusMessage = "Loading update history...";
        _cts = new CancellationTokenSource();
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                Import-Module PSWindowsUpdate -ErrorAction Stop
                Write-Host '=== Last 20 installed updates ==='
                Get-WUHistory -Last 20 |
                    Select-Object Date, Result, KB, Title |
                    Format-Table -AutoSize -Wrap | Out-String -Width 250
            ", cancellationToken: _cts.Token);
            StatusMessage = "Done";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private async Task CheckPendingRebootAsync()
    {
        IsBusy = true; IsProgressIndeterminate = true;
        StatusMessage = "Checking pending reboot...";
        _cts = new CancellationTokenSource();
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                $pending = $false
                $reasons = @()
                if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') {
                    $pending = $true; $reasons += 'Component Based Servicing'
                }
                if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') {
                    $pending = $true; $reasons += 'Windows Update'
                }
                try {
                    $p = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction Stop).PendingFileRenameOperations
                    if ($p) { $pending = $true; $reasons += 'Pending file rename operations' }
                } catch {}
                if ($pending) { ""REBOOT REQUIRED - reasons: $($reasons -join ', ')"" }
                else         { 'No pending reboot detected.' }
            ", cancellationToken: _cts.Token);
            StatusMessage = "Done";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private async Task InstallUpdatesAsync()
    {
        if (!AdminHelper.IsElevated())
        {
            StatusMessage = "Admin required. Relaunching elevated...";
            if (AdminHelper.RelaunchAsAdmin()) System.Windows.Application.Current.Shutdown();
            return;
        }
        IsBusy = true;
        StatusMessage = "Installing updates (do not reboot)...";
        _cts = new CancellationTokenSource();
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                Import-Module PSWindowsUpdate -ErrorAction Stop
                Install-WindowsUpdate -MicrosoftUpdate -AcceptAll -IgnoreReboot -Verbose
            ", cancellationToken: _cts.Token);
            StatusMessage = "Installation finished";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
