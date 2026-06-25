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
        // Plain resolvers (IPv4 + IPv6).
        new DnsPreset { Name = "Google",      Primary = "8.8.8.8",         Secondary = "8.8.4.4",
            PrimaryV6 = "2001:4860:4860::8888", SecondaryV6 = "2001:4860:4860::8844", Description = "Google Public DNS — fast, no filtering" },
        new DnsPreset { Name = "Cloudflare",  Primary = "1.1.1.1",         Secondary = "1.0.0.1",
            PrimaryV6 = "2606:4700:4700::1111", SecondaryV6 = "2606:4700:4700::1001", Description = "Cloudflare — privacy-focused, no filtering" },
        new DnsPreset { Name = "Quad9",       Primary = "9.9.9.9",         Secondary = "149.112.112.112",
            PrimaryV6 = "2620:fe::fe", SecondaryV6 = "2620:fe::9", Description = "Quad9 — blocks known malware domains (secure by default)" },
        new DnsPreset { Name = "OpenDNS",     Primary = "208.67.222.222",  Secondary = "208.67.220.220",
            PrimaryV6 = "2620:119:35::35", SecondaryV6 = "2620:119:53::53", Description = "Cisco OpenDNS — standard resolver" },

        // Filtering variants.
        new DnsPreset { Name = "Cloudflare — Malware blocking", Primary = "1.1.1.2", Secondary = "1.0.0.2",
            PrimaryV6 = "2606:4700:4700::1112", SecondaryV6 = "2606:4700:4700::1002", Description = "Cloudflare 1.1.1.2 — blocks malware" },
        new DnsPreset { Name = "Cloudflare — Family (malware + adult)", Primary = "1.1.1.3", Secondary = "1.0.0.3",
            PrimaryV6 = "2606:4700:4700::1113", SecondaryV6 = "2606:4700:4700::1003", Description = "Cloudflare 1.1.1.3 — blocks malware and adult content" },
        new DnsPreset { Name = "AdGuard DNS — Ad blocking", Primary = "94.140.14.14", Secondary = "94.140.15.15",
            PrimaryV6 = "2a10:50c0::ad1:ff", SecondaryV6 = "2a10:50c0::ad2:ff", Description = "AdGuard — blocks ads and trackers" },
        new DnsPreset { Name = "AdGuard DNS — Family", Primary = "94.140.14.15", Secondary = "94.140.15.16",
            PrimaryV6 = "2a10:50c0::bad1:ff", SecondaryV6 = "2a10:50c0::bad2:ff", Description = "AdGuard — ads, trackers, and adult content" },
        new DnsPreset { Name = "OpenDNS FamilyShield", Primary = "208.67.222.123", Secondary = "208.67.220.123",
            PrimaryV6 = "2620:119:35::123", SecondaryV6 = "2620:119:53::123", Description = "OpenDNS FamilyShield — blocks adult content" },

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
    /// A point-in-time snapshot of an adapter's DNS configuration for BOTH families, so a
    /// change can be reverted exactly. An empty list for a family means that family was on
    /// automatic (DHCP) at capture time and is restored by clearing it back to DHCP.
    /// </summary>
    public sealed record DnsSnapshot(IReadOnlyList<string> V4, IReadOnlyList<string> V6)
    {
        public static readonly DnsSnapshot Empty = new([], []);
    }

    /// <summary>
    /// Captures the current IPv4 DNS server addresses of the active adapter so a
    /// change can be reverted to the exact previous configuration. Returns an empty
    /// list when the adapter is on automatic (DHCP) — restoring that snapshot resets
    /// to DHCP rather than re-applying static servers.
    /// </summary>
    public async Task<IReadOnlyList<string>> CaptureCurrentServersAsync(CancellationToken ct = default)
        => (await CaptureSnapshotAsync(ct).ConfigureAwait(false)).V4;

    /// <summary>
    /// Captures the current DNS server addresses of the active adapter for BOTH IPv4 and
    /// IPv6, so a change that programs both families can be fully reverted. An empty list
    /// for a family means it was on automatic (DHCP).
    /// </summary>
    public async Task<DnsSnapshot> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Tag each address with its family so one query returns both, in order.
            const string script = ActiveAdapterSelector + """

                if ($adapter) {
                    foreach ($fam in @('IPv4','IPv6')) {
                        $dns = Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -AddressFamily $fam
                        foreach ($a in $dns.ServerAddresses) { "$fam=$a" }
                    }
                }
                """;

            Collection<PSObject> results = await _ps.RunAsync(script, cancellationToken: ct)
                .ConfigureAwait(false);

            List<string> v4 = [], v6 = [];
            foreach (var r in results)
            {
                var line = r?.ToString();
                if (string.IsNullOrWhiteSpace(line)) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var fam = line[..eq];
                var addr = line[(eq + 1)..];
                if (!IPAddress.TryParse(addr, out _)) continue;
                if (fam == "IPv4") v4.Add(addr);
                else if (fam == "IPv6") v6.Add(addr);
            }
            return new DnsSnapshot(v4, v6);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Restores DNS to a previously captured IPv4-only set of server addresses. An empty
    /// snapshot means the adapter was on DHCP, so this resets to automatic.
    /// </summary>
    public Task RestoreServersAsync(IReadOnlyList<string> servers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        return RestoreSnapshotAsync(new DnsSnapshot(servers, []), ct);
    }

    /// <summary>
    /// Restores DNS to a previously captured snapshot for BOTH families. Resets the adapter
    /// to DHCP first (clearing anything that was applied since, including filtering IPv6
    /// resolvers a v4-only restore would otherwise leave behind), then re-applies the
    /// captured static servers per family. A fully-empty snapshot therefore restores DHCP.
    /// </summary>
    public async Task RestoreSnapshotAsync(DnsSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Validate every captured address before applying — the snapshot should already be
        // clean, but never interpolate an unvalidated value into a script.
        foreach (var server in snapshot.V4.Concat(snapshot.V6))
        {
            if (!IPAddress.TryParse(server, out _))
                throw new ArgumentException($"Invalid DNS address in snapshot: '{server}'", nameof(snapshot));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int ifIndex = await GetActiveInterfaceIndexAsync(ct).ConfigureAwait(false);
            var script = new System.Text.StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            // Clear BOTH families first so any servers applied since (incl. IPv6) are removed.
            script.AppendLine($"Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -ResetServerAddresses -ErrorAction Stop");
            if (snapshot.V4.Count > 0)
            {
                var v4 = string.Join(",", snapshot.V4.Select(s => $"\"{s}\""));
                script.AppendLine($"Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -ServerAddresses @({v4}) -ErrorAction Stop");
            }
            if (snapshot.V6.Count > 0)
            {
                var v6 = string.Join(",", snapshot.V6.Select(s => $"\"{s}\""));
                script.AppendLine($"Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -ServerAddresses @({v6}) -ErrorAction Stop");
            }

            await _ps.RunAsync(script.ToString(), cancellationToken: ct).ConfigureAwait(false);
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
    /// Sets the IPv4 DNS pair and, when supplied, the IPv6 pair on the active adapter.
    /// IPv6 is set as a separate address family so a v4-only preset leaves IPv6 untouched.
    /// All addresses are validated before any script runs.
    /// </summary>
    public async Task SetDnsAsync(string primary, string secondary, string primaryV6, string secondaryV6, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(primary, out _))
            throw new ArgumentException($"Invalid primary DNS address: '{primary}'", nameof(primary));
        if (!IPAddress.TryParse(secondary, out _))
            throw new ArgumentException($"Invalid secondary DNS address: '{secondary}'", nameof(secondary));

        var hasV6 = !string.IsNullOrEmpty(primaryV6);
        if (hasV6)
        {
            if (!IPAddress.TryParse(primaryV6, out _))
                throw new ArgumentException($"Invalid primary IPv6 DNS address: '{primaryV6}'", nameof(primaryV6));
            if (!string.IsNullOrEmpty(secondaryV6) && !IPAddress.TryParse(secondaryV6, out _))
                throw new ArgumentException($"Invalid secondary IPv6 DNS address: '{secondaryV6}'", nameof(secondaryV6));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int ifIndex = await GetActiveInterfaceIndexAsync(ct).ConfigureAwait(false);
            // Set the IPv4 family explicitly so it cannot clobber the IPv6 entries set below.
            var script = new System.Text.StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            script.AppendLine($"Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -ServerAddresses @(\"{primary}\",\"{secondary}\") -ErrorAction Stop");
            if (hasV6)
            {
                var v6List = string.IsNullOrEmpty(secondaryV6) ? $"\"{primaryV6}\"" : $"\"{primaryV6}\",\"{secondaryV6}\"";
                // IPv6 is BEST-EFFORT: on a machine with IPv6 disabled, setting an IPv6 DNS
                // throws. We must NOT fail the whole apply (IPv4 already succeeded) or the UI
                // would report failure while IPv4 silently changed, with Undo never offered.
                // The address family is implied by the address shape; a separate call keeps it
                // additive to the IPv4 set above. A warning is emitted but does not terminate.
                script.AppendLine(
                    $"try {{ Set-DnsClientServerAddress -InterfaceIndex {ifIndex} -ServerAddresses @({v6List}) -ErrorAction Stop }} " +
                    "catch { Write-Warning \"IPv6 DNS not applied: $($_.Exception.Message)\" }");
            }

            await _ps.RunAsync(script.ToString(), cancellationToken: ct).ConfigureAwait(false);
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
