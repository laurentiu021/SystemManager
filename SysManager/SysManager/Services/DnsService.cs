// SysManager · DnsService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Net;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Manages DNS server configuration: presets, current state, apply, and reset.
/// </summary>
public sealed class DnsService : IDisposable
{
    private readonly IPowerShellRunner _ps;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DnsService(IPowerShellRunner ps) => _ps = ps;

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Returns the built-in DNS presets.
    /// </summary>
    public List<DnsPreset> GetPresets() =>
    [
        new DnsPreset { Name = "Google",      Primary = "8.8.8.8",         Secondary = "8.8.4.4",         Description = "Google Public DNS" },
        new DnsPreset { Name = "Cloudflare",  Primary = "1.1.1.1",         Secondary = "1.0.0.1",         Description = "Cloudflare DNS — privacy-focused" },
        new DnsPreset { Name = "Quad9",       Primary = "9.9.9.9",         Secondary = "149.112.112.112", Description = "Quad9 — malware blocking" },
        new DnsPreset { Name = "OpenDNS",     Primary = "208.67.222.222",  Secondary = "208.67.220.220",  Description = "Cisco OpenDNS" },
        new DnsPreset { Name = "Automatic (DHCP)", Primary = "",           Secondary = "",                Description = "Use DHCP-assigned DNS" },
    ];

    /// <summary>
    /// Reads the current DNS server addresses from the first active network adapter.
    /// </summary>
    public async Task<string> GetCurrentDnsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            const string script = ActiveAdapterSelector + """

                if ($adapter) {
                    $dns = Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4
                    if ($dns.ServerAddresses.Count -gt 0) { $dns.ServerAddresses -join ', ' }
                    else { 'Automatic (DHCP)' }
                } else { 'No active adapter' }
                """;

            Collection<PSObject> results = await _ps.RunAsync(script, cancellationToken: ct)
                .ConfigureAwait(false);

            return results.Count > 0 ? results[0]?.ToString() ?? "Unknown" : "Unknown";
        }
        finally { _gate.Release(); }
    }

    // Single source of truth for "the active adapter": prefer a non-virtual adapter
    // that is Up, fall back to any Up adapter, and always order by ifIndex so the
    // SAME adapter is chosen for reading, snapshotting, and mutating. Without this,
    // display/capture and set/reset/restore could target different NICs on a
    // multi-adapter machine (Wi-Fi + Ethernet + VPN), breaking reversibility.
    private const string ActiveAdapterSelector =
        "$adapter = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.Virtual -eq $false } | Sort-Object -Property ifIndex | Select-Object -First 1; " +
        "if (-not $adapter) { $adapter = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Sort-Object -Property ifIndex | Select-Object -First 1 }";

    /// <summary>
    /// Detects the interface index of the active network adapter using the shared
    /// <see cref="ActiveAdapterSelector"/> rule. Uses the integer index to avoid
    /// command injection through adapter names.
    /// </summary>
    private async Task<int> GetActiveInterfaceIndexAsync(CancellationToken ct)
    {
        const string script = ActiveAdapterSelector + """

            if ($adapter) { $adapter.ifIndex }
            """;

        Collection<PSObject> results = await _ps.RunAsync(script, cancellationToken: ct)
            .ConfigureAwait(false);

        if (results.Count > 0 && int.TryParse(results[0]?.ToString(), out var index))
            return index;

        throw new InvalidOperationException("No active network adapter found.");
    }

    /// <summary>
    /// Captures the current IPv4 DNS server addresses of the active adapter so a
    /// change can be reverted to the exact previous configuration. Returns an empty
    /// list when the adapter is on automatic (DHCP) — restoring that snapshot resets
    /// to DHCP rather than re-applying static servers.
    /// </summary>
    public async Task<IReadOnlyList<string>> CaptureCurrentServersAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            const string script = ActiveAdapterSelector + """

                if ($adapter) {
                    $dns = Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4
                    $dns.ServerAddresses
                }
                """;

            Collection<PSObject> results = await _ps.RunAsync(script, cancellationToken: ct)
                .ConfigureAwait(false);

            return results
                .Select(r => r?.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s) && IPAddress.TryParse(s, out _))
                .Select(s => s!)
                .ToList();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Restores DNS to a previously captured set of server addresses. An empty
    /// snapshot means the adapter was on DHCP, so this resets to automatic.
    /// </summary>
    public async Task RestoreServersAsync(IReadOnlyList<string> servers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(servers);

        if (servers.Count == 0)
        {
            await ResetToDhcpAsync(ct).ConfigureAwait(false);
            return;
        }

        // Validate every captured address before applying — the snapshot should
        // already be clean, but never interpolate an unvalidated value into a script.
        foreach (var server in servers)
        {
            if (!IPAddress.TryParse(server, out _))
                throw new ArgumentException($"Invalid DNS address in snapshot: '{server}'", nameof(servers));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int ifIndex = await GetActiveInterfaceIndexAsync(ct).ConfigureAwait(false);
            string joined = string.Join(",", servers.Select(s => $"\"{s}\""));
            // -ErrorAction Stop makes a non-terminating cmdlet failure (denied
            // privilege, adapter down, RPC failure) terminating, so RunAsync's
            // EndInvoke throws instead of the call silently reporting success.
            string script = $"""
                $ErrorActionPreference = 'Stop'
                Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -ServerAddresses @({joined}) -ErrorAction Stop
                """;

            await _ps.RunAsync(script, cancellationToken: ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Sets the DNS server addresses on the active network adapter.
    /// </summary>
    public async Task SetDnsAsync(string primary, string secondary, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(primary, out _))
            throw new ArgumentException($"Invalid primary DNS address: '{primary}'", nameof(primary));
        if (!IPAddress.TryParse(secondary, out _))
            throw new ArgumentException($"Invalid secondary DNS address: '{secondary}'", nameof(secondary));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int ifIndex = await GetActiveInterfaceIndexAsync(ct).ConfigureAwait(false);
            string script = $"""
                $ErrorActionPreference = 'Stop'
                Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -ServerAddresses @("{primary}","{secondary}") -ErrorAction Stop
                """;

            await _ps.RunAsync(script, cancellationToken: ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Resets DNS to automatic (DHCP) on the active network adapter.
    /// </summary>
    public async Task ResetToDhcpAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int ifIndex = await GetActiveInterfaceIndexAsync(ct).ConfigureAwait(false);
            string script = $"""
                $ErrorActionPreference = 'Stop'
                Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -ResetServerAddresses -ErrorAction Stop
                """;

            await _ps.RunAsync(script, cancellationToken: ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
