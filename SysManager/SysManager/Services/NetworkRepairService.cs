// SysManager · NetworkRepairService
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Runs common network repair commands: DNS flush, Winsock reset, TCP/IP reset.
/// Each method captures stdout/stderr and returns a <see cref="NetworkRepairResult"/>.
/// </summary>
public class NetworkRepairService
{
    private readonly PowerShellRunner _ps;

    public NetworkRepairService(PowerShellRunner ps) => _ps = ps;

    /// <summary>
    /// Flush the DNS resolver cache. Does not require a reboot.
    /// </summary>
    public async Task<NetworkRepairResult> FlushDnsAsync(CancellationToken ct = default)
    {
        var output = new List<string>();
        void OnLine(PowerShellLine line) => output.Add(line.Text);
        _ps.LineReceived += OnLine;
        try
        {
            var exit = await _ps.RunProcessAsync("ipconfig.exe", "/flushdns", ct)
                .ConfigureAwait(false);
            return new NetworkRepairResult(
                "DNS Flush",
                exit == 0,
                string.Join(Environment.NewLine, output),
                NeedsReboot: false);
        }
        finally { _ps.LineReceived -= OnLine; }
    }

    /// <summary>
    /// Reset the Winsock catalog. Requires a reboot to take effect.
    /// </summary>
    public async Task<NetworkRepairResult> ResetWinsockAsync(CancellationToken ct = default)
    {
        var output = new List<string>();
        void OnLine(PowerShellLine line) => output.Add(line.Text);
        _ps.LineReceived += OnLine;
        try
        {
            var exit = await _ps.RunProcessAsync("netsh.exe", "winsock reset", ct)
                .ConfigureAwait(false);
            return new NetworkRepairResult(
                "Winsock Reset",
                exit == 0,
                string.Join(Environment.NewLine, output),
                NeedsReboot: true);
        }
        finally { _ps.LineReceived -= OnLine; }
    }

    /// <summary>
    /// Reset the TCP/IP stack. Requires a reboot to take effect.
    /// </summary>
    public async Task<NetworkRepairResult> ResetTcpIpAsync(CancellationToken ct = default)
    {
        var output = new List<string>();
        void OnLine(PowerShellLine line) => output.Add(line.Text);
        _ps.LineReceived += OnLine;
        try
        {
            var exit = await _ps.RunProcessAsync("netsh.exe", "int ip reset", ct)
                .ConfigureAwait(false);
            return new NetworkRepairResult(
                "TCP/IP Reset",
                exit == 0,
                string.Join(Environment.NewLine, output),
                NeedsReboot: true);
        }
        finally { _ps.LineReceived -= OnLine; }
    }
}
