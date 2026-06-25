// SysManager · SystemFixesViewModel — one-click repairs for common Windows breakages
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the System Fixes tab. Surfaces a small set of well-known one-click
/// repairs (Windows Update, WinGet) plus a secure shortcut to the built-in auto-logon
/// dialog. Each repair confirms first, runs through <see cref="SystemFixService"/>,
/// streams output, and reports success/failure honestly. Repairs require administrator
/// rights; the tab shows the standard elevation banner. (Network-stack reset lives on the
/// Network Repair tab, which owns Winsock/TCP-IP/DNS-flush, so it is not duplicated here.)
/// </summary>
public sealed partial class SystemFixesViewModel : ViewModelBase
{
    private readonly SystemFixService _service;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _output = "";

    public SystemFixesViewModel(SystemFixService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        StatusMessage = "Pick a repair. Each asks for confirmation before it runs.";
        _service.LineReceived += OnLine;
        PropertyChanged += OnVmPropertyChanged;
    }

    private bool CanRunFix => !IsBusy && IsElevated;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy) or nameof(IsElevated))
        {
            ResetWindowsUpdateCommand.NotifyCanExecuteChanged();
            ReinstallWinGetCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnLine(PowerShellLine line)
    {
        var text = line.Text;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            Output += (Output.Length == 0 ? "" : Environment.NewLine) + text);
    }

    [RelayCommand(CanExecute = nameof(CanRunFix))]
    private Task ResetWindowsUpdateAsync() => RunFixAsync(
        "Reset Windows Update?",
        "This stops the Windows Update services, clears their cache folders " +
        "(SoftwareDistribution and catroot2, renamed so Windows rebuilds them), and restarts " +
        "the services. Pending updates will re-download. A reboot is recommended afterwards.\n\nContinue?",
        ct => _service.ResetWindowsUpdateAsync(ct));

    [RelayCommand(CanExecute = nameof(CanRunFix))]
    private Task ReinstallWinGetAsync() => RunFixAsync(
        "Reinstall WinGet?",
        "This re-registers the Windows Package Manager (App Installer) for your account, " +
        "which fixes most cases where app installs or uninstalls fail. No reboot needed.\n\nContinue?",
        ct => _service.ReinstallWinGetAsync(ct));

    private async Task RunFixAsync(string title, string message, Func<CancellationToken, Task<SystemFixResult>> fix)
    {
        if (!DialogService.Instance.Confirm(message, title))
        {
            StatusMessage = "Cancelled.";
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = true;
        Output = "";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var result = await fix(_cts.Token).ConfigureAwait(true);
            if (result.Success)
            {
                StatusMessage = result.NeedsReboot
                    ? $"{result.FixName} completed — reboot to finish."
                    : $"{result.FixName} completed.";
                ToastService.Instance.Show(result.FixName, result.NeedsReboot ? "Done — reboot recommended." : "Done.");
            }
            else
            {
                StatusMessage = $"{result.FixName} did not complete — see the output for details.";
            }
            Log.Information("SystemFix: {Fix} success={Success}", result.FixName, result.Success);
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void OpenAutologin()
    {
        // Auto-logon is configured through the built-in netplwiz dialog, which stores the
        // credential securely (LSA secret) — SysManager never writes a plaintext password.
        try
        {
            Process.Start(new ProcessStartInfo("netplwiz.exe") { UseShellExecute = true })?.Dispose();
            StatusMessage = "Opened User Accounts (netplwiz). Untick \"Users must enter a user name and password\" to enable auto sign-in.";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning("SystemFix: could not open netplwiz: {Error}", ex.Message);
            StatusMessage = "Couldn't open the User Accounts dialog.";
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
            _service.LineReceived -= OnLine;
            PropertyChanged -= OnVmPropertyChanged;
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
