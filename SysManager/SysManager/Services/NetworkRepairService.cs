// SysManager · NetworkRepairService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Runs common network repair commands: DNS flush, Winsock reset, TCP/IP reset.
/// Each method captures stdout/stderr and returns a <see cref="NetworkRepairResult"/>.
/// </summary>
public sealed class NetworkRepairService : IDisposable
{
    private readonly IPowerShellRunner _ps;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Idempotent, and paired with the guarded releases below: this is a DI singleton disposed at
    // process exit, so a request in flight at shutdown would otherwise release a disposed gate from a
    // finally block and log an error on a clean exit.
    private bool _disposed;

    public NetworkRepairService(IPowerShellRunner ps) => _ps = ps;

    /// <inheritdoc />
    /// <summary>
    /// Releases the gate unless <see cref="Dispose"/> has already claimed it. Releasing a disposed
    /// <see cref="SemaphoreSlim"/> throws, and every call site is a <c>finally</c> block, where that
    /// would replace a clean shutdown — or a real error — with an unhandled exception.
    /// </summary>
    private void ReleaseGate()
    {
        if (_disposed) return;
        try { _gate.Release(); }
        catch (ObjectDisposedException) { /* disposed mid-request at shutdown */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

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
        finally { _ps.LineReceived -= OnLine; ReleaseGate(); }
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
        finally { _ps.LineReceived -= OnLine; ReleaseGate(); }
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
        finally { _ps.LineReceived -= OnLine; ReleaseGate(); }
    }
}
