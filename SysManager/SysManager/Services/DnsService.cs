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
    private readonly PowerShellRunner _ps;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DnsService(PowerShellRunner ps) => _ps = ps;

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
            const string script = """
                $adapter = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1
                if ($adapter) {
                    $dns = Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4
                    if ($dns.ServerAddresses.Count -gt 0) {
                        $dns.ServerAddresses -join ', '
                    } else {
                        'Automatic (DHCP)'
                    }
                } else {
                    'No active adapter'
                }
                """;

            Collection<PSObject> results = await _ps.RunAsync(script, cancellationToken: ct)
                .ConfigureAwait(false);

            return results.Count > 0 ? results[0]?.ToString() ?? "Unknown" : "Unknown";
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Detects the interface index of the first active network adapter.
    /// Uses the integer index to avoid command injection through adapter names.
    /// </summary>
    private async Task<int> GetActiveInterfaceIndexAsync(CancellationToken ct)
    {
        const string script = """
            Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1 -ExpandProperty ifIndex
            """;

        Collection<PSObject> results = await _ps.RunAsync(script, cancellationToken: ct)
            .ConfigureAwait(false);

        if (results.Count > 0 && int.TryParse(results[0]?.ToString(), out var index))
            return index;

        throw new InvalidOperationException("No active network adapter found.");
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
                Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -ServerAddresses @("{primary}","{secondary}")
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
                Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -ResetServerAddresses
                """;

            await _ps.RunAsync(script, cancellationToken: ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
