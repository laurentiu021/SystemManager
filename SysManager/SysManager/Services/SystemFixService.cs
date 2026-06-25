// SysManager · SystemFixService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// One-click repairs for common Windows breakages — Windows Update, the network stack,
/// and WinGet. All commands run through the <see cref="IPowerShellRunner"/> seam (so the
/// orchestration is substitutable in tests) and stream their output via
/// <see cref="IPowerShellRunner.LineReceived"/> for live display.
///
/// SECURITY: every script is hard-coded — no user input is interpolated. These repairs
/// mutate system state and require administrator rights; each is gated behind an explicit
/// confirmation in the ViewModel. Account auto-logon is intentionally NOT implemented here
/// as a credential write (that would store the password in plaintext in the registry);
/// the ViewModel instead opens the built-in <c>netplwiz</c> dialog, which sets it securely.
/// </summary>
public sealed class SystemFixService : IDisposable
{
    private readonly IPowerShellRunner _ps;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SystemFixService(IPowerShellRunner ps) => _ps = ps;

    public event Action<PowerShellLine>? LineReceived
    {
        add => _ps.LineReceived += value;
        remove => _ps.LineReceived -= value;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Resets Windows Update: stops the services, renames SoftwareDistribution and
    /// catroot2 (so Windows rebuilds them), then restarts the services. Requires admin
    /// and a reboot afterwards for best results.
    /// </summary>
    public Task<SystemFixResult> ResetWindowsUpdateAsync(CancellationToken ct = default)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $services = 'wuauserv','cryptSvc','bits','msiserver'
            foreach ($s in $services) { Stop-Service -Name $s -Force -ErrorAction SilentlyContinue }
            $sd = Join-Path $env:SystemRoot 'SoftwareDistribution'
            $cr = Join-Path $env:SystemRoot 'System32\catroot2'
            if (Test-Path $sd) { Rename-Item $sd "$sd.old.$((Get-Date).ToString('yyyyMMddHHmmss'))" -ErrorAction SilentlyContinue }
            if (Test-Path $cr) { Rename-Item $cr "$cr.old.$((Get-Date).ToString('yyyyMMddHHmmss'))" -ErrorAction SilentlyContinue }
            foreach ($s in $services) { Start-Service -Name $s -ErrorAction SilentlyContinue }
            'Windows Update components reset. A reboot is recommended.'
            """;
        return RunFixAsync("Reset Windows Update", script, needsReboot: true, ct);
    }

    // Network-stack reset (Winsock / TCP-IP / DNS flush) intentionally lives ONLY on the
    // Network Repair tab — it is not duplicated here. See NetworkRepairService.

    /// <summary>
    /// Re-registers the WinGet (App Installer) package so app installs/uninstalls work
    /// again. Requires admin. No reboot needed.
    /// </summary>
    public Task<SystemFixResult> ReinstallWinGetAsync(CancellationToken ct = default)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $pkg = Get-AppxPackage -AllUsers Microsoft.DesktopAppInstaller -ErrorAction SilentlyContinue |
                   Select-Object -First 1
            if ($pkg) {
                Add-AppxPackage -DisableDevelopmentMode -Register `
                    (Join-Path $pkg.InstallLocation 'AppXManifest.xml') -ErrorAction Stop
                'WinGet (App Installer) re-registered successfully.'
            } else {
                throw 'App Installer package not found. Install "App Installer" from the Microsoft Store.'
            }
            """;
        return RunFixAsync("Reinstall WinGet", script, needsReboot: false, ct);
    }

    private async Task<SystemFixResult> RunFixAsync(string name, string script, bool needsReboot, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void OnLine(PowerShellLine line) => output.Enqueue(line.Text);
        _ps.LineReceived += OnLine;
        try
        {
            // RunAsync throws on a terminating error (ErrorActionPreference=Stop / throw),
            // so a normal return means the script ran to completion.
            await _ps.RunAsync(script, cancellationToken: ct).ConfigureAwait(false);
            return new SystemFixResult(name, Success: true, string.Join(Environment.NewLine, output), needsReboot);
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            output.Enqueue(ex.Message);
            return new SystemFixResult(name, Success: false, string.Join(Environment.NewLine, output), needsReboot);
        }
        finally
        {
            _ps.LineReceived -= OnLine;
            _gate.Release();
        }
    }
}
