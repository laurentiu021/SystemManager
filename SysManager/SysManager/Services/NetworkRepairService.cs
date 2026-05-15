// SysManager · NetworkRepairService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Runs common network repair commands: DNS flush, Winsock reset, TCP/IP reset.
/// Each method captures stdout/stderr and returns a <see cref="NetworkRepairResult"/>.
/// </summary>
public sealed class NetworkRepairService
{
    private readonly PowerShellRunner _ps;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NetworkRepairService(PowerShellRunner ps) => _ps = ps;

    /// <summary>
    /// Flush the DNS resolver cache. Does not require a reboot.
    /// </summary>
    public async Task<NetworkRepairResult> FlushDnsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void OnLine(PowerShellLine line) => output.Enqueue(line.Text);
        _ps.LineReceived += OnLine;
        try
        {
            var exit = await _ps.RunProcessAsync("ipconfig.exe", "/flushdns", ct, PowerShellRunner.OemEncoding)
                .ConfigureAwait(false);
            return new NetworkRepairResult(
                "DNS Flush",
                exit == 0,
                string.Join(Environment.NewLine, output),
                NeedsReboot: false);
        }
        finally { _ps.LineReceived -= OnLine; _gate.Release(); }
    }

    /// <summary>
    /// Reset the Winsock catalog. Requires a reboot to take effect.
    /// </summary>
    public async Task<NetworkRepairResult> ResetWinsockAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void OnLine(PowerShellLine line) => output.Enqueue(line.Text);
        _ps.LineReceived += OnLine;
        try
        {
            var exit = await _ps.RunProcessAsync("netsh.exe", "winsock reset", ct, PowerShellRunner.OemEncoding)
                .ConfigureAwait(false);
            return new NetworkRepairResult(
                "Winsock Reset",
                exit == 0,
                string.Join(Environment.NewLine, output),
                NeedsReboot: true);
        }
        finally { _ps.LineReceived -= OnLine; _gate.Release(); }
    }

    /// <summary>
    /// Reset the TCP/IP stack. Requires a reboot to take effect.
    /// </summary>
    public async Task<NetworkRepairResult> ResetTcpIpAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void OnLine(PowerShellLine line) => output.Enqueue(line.Text);
        _ps.LineReceived += OnLine;
        try
        {
            var exit = await _ps.RunProcessAsync("netsh.exe", "int ip reset", ct, PowerShellRunner.OemEncoding)
                .ConfigureAwait(false);
            return new NetworkRepairResult(
                "TCP/IP Reset",
                exit == 0,
                string.Join(Environment.NewLine, output),
                NeedsReboot: true);
        }
        finally { _ps.LineReceived -= OnLine; _gate.Release(); }
    }
}
