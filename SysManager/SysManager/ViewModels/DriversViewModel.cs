// SysManager · DriversViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.Input;
using SysManager.Services;

namespace SysManager.ViewModels;

public partial class DriversViewModel : ViewModelBase
{
    private readonly PowerShellRunner _runner;
    private CancellationTokenSource? _cts;

    public ConsoleViewModel Console { get; } = new();

    public DriversViewModel(PowerShellRunner runner)
    {
        _runner = runner;
        _runner.LineReceived += l => Console.Append(l);
    }

    [RelayCommand]
    private async Task ListDriversAsync()
    {
        IsBusy = true; IsProgressIndeterminate = true;
        StatusMessage = "Listing drivers...";
        _cts = new CancellationTokenSource();
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                Get-CimInstance Win32_PnPSignedDriver |
                  Where-Object { $_.DeviceName -and $_.DriverVersion } |
                  Select-Object DeviceName, DriverVersion, Manufacturer, DriverDate |
                  Sort-Object DeviceName |
                  Format-Table -AutoSize | Out-String -Width 200
            ", cancellationToken: _cts.Token);
            StatusMessage = "Done";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private async Task CheckWindowsUpdateDriversAsync()
    {
        IsBusy = true; IsProgressIndeterminate = true;
        StatusMessage = "Checking driver updates via Windows Update...";
        _cts = new CancellationTokenSource();
        try
        {
            await _runner.RunScriptViaPwshAsync(@"
                if (-not (Get-Module -ListAvailable -Name PSWindowsUpdate)) {
                    'PSWindowsUpdate not installed — use the Windows Update tab to install it.'
                    return
                }
                Import-Module PSWindowsUpdate
                Get-WindowsUpdate -MicrosoftUpdate -Category 'Drivers' |
                    Format-Table -AutoSize | Out-String -Width 200
            ", cancellationToken: _cts.Token);
            StatusMessage = "Done";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
