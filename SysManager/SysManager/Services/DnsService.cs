// SysManager · DnsService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Net;
using System.Net.Sockets;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Manages DNS server configuration: presets, current state, apply, and reset.
/// </summary>
public sealed class DnsService : IDisposable
{
    private const string MutationPreconditionFailureMarker =
        "SYSMANAGER_DNS_PRECONDITION_FAILED";

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
        catch (RuntimeException ex)
        {
            Log.Debug("DNS status read failed: {Error}", ex.Message);
            return "Unavailable";
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
    /// Identifies whether a DNS address family uses automatic configuration or a static override.
    /// </summary>
    public enum DnsConfigurationSource
    {
        Unknown,
        Automatic,
        Static,
    }

    /// <summary>
    /// A point-in-time snapshot of an adapter's DNS configuration for both families. Server
    /// addresses alone cannot distinguish static DNS from addresses supplied automatically, so
    /// each family's configuration source is captured too. <paramref name="IfIndex"/> pins the
    /// adapter selected at capture time, while <paramref name="InterfaceGuid"/> prevents a
    /// recycled index from targeting a different adapter. Zero means "not captured" and falls
    /// back to dynamic lookup for legacy callers.
    /// </summary>
    public sealed record DnsSnapshot(
        IReadOnlyList<string> V4,
        IReadOnlyList<string> V6,
        int IfIndex = 0,
        Guid? InterfaceGuid = null,
        DnsConfigurationSource V4Source = DnsConfigurationSource.Unknown,
        DnsConfigurationSource V6Source = DnsConfigurationSource.Unknown)
    {
        public static readonly DnsSnapshot Empty = new([], []);
    }

    /// <summary>
    /// Indicates that the captured adapter and DNS preconditions could not be verified.
    /// The guarded operation stopped before its first DNS mutation.
    /// </summary>
    public sealed class DnsMutationPreconditionException : InvalidOperationException
    {
        public DnsMutationPreconditionException(string? detail = null)
            : base(string.IsNullOrWhiteSpace(detail)
                ? "The network adapter or DNS configuration changed, or its current state could not be verified, before the DNS operation started."
                : $"The network adapter or DNS configuration changed, or its current state could not be verified, before the DNS operation started. Details: {detail}")
        {
        }
    }

    /// <summary>
    /// Captures the current IPv4 DNS server addresses of the active adapter so a
    /// change can be reverted to the exact previous configuration. Returns an empty
    /// list when the adapter is on automatic (DHCP) — restoring that snapshot resets
    /// to DHCP rather than re-applying static servers.
    /// </summary>
    public async Task<IReadOnlyList<string>> CaptureCurrentServersAsync(CancellationToken ct = default)
    {
        var snapshot = await CaptureSnapshotAsync(ct).ConfigureAwait(false);
        return snapshot.V4Source == DnsConfigurationSource.Static ? snapshot.V4 : [];
    }

    /// <summary>
    /// Captures the current DNS server addresses and configuration source of the active
    /// adapter for BOTH IPv4 and IPv6, so a change can be fully reverted without turning
    /// DHCP-provided addresses into persistent static DNS.
    /// </summary>
    public async Task<DnsSnapshot> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // NameServer is the per-adapter static override; an absent/empty value means
            // the effective addresses came from automatic configuration. Capture this
            // source explicitly because DHCP can still report non-empty DNS addresses.
            const string script = "$ErrorActionPreference = 'Stop'; " + ActiveAdapterSelector + """

                if ($adapter) {
                    $ifIndex = [int]$adapter.ifIndex
                    $interfaceGuid = [Guid]$adapter.InterfaceGuid
                    "IFINDEX=$ifIndex"
                    "IFGUID=$($interfaceGuid.ToString('D'))"
                    $guidKey = $interfaceGuid.ToString('B')
                    $registryPaths = @{
                        IPv4 = "SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$guidKey"
                        IPv6 = "SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\$guidKey"
                    }
                    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
                        [Microsoft.Win32.RegistryHive]::LocalMachine,
                        [Microsoft.Win32.RegistryView]::Default)
                    try {
                        foreach ($fam in @('IPv4','IPv6')) {
                            $key = $registry.OpenSubKey($registryPaths[$fam], $false)
                            if ($null -eq $key) {
                                throw "DNS registry state for $fam is unavailable."
                            }
                            try {
                                $nameServerBefore = $key.GetValue(
                                    'NameServer',
                                    $null,
                                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                                if ($null -ne $nameServerBefore -and $nameServerBefore -isnot [string]) {
                                    throw "DNS registry state for $fam has an unexpected value type."
                                }

                                $dns = Get-DnsClientServerAddress -InterfaceIndex $ifIndex -AddressFamily $fam -ErrorAction Stop

                                $nameServerAfter = $key.GetValue(
                                    'NameServer',
                                    $null,
                                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                                if ($null -ne $nameServerAfter -and $nameServerAfter -isnot [string]) {
                                    throw "DNS registry state for $fam has an unexpected value type."
                                }
                            }
                            finally {
                                $key.Dispose()
                            }
                            if (-not [string]::Equals(
                                [string]$nameServerBefore,
                                [string]$nameServerAfter,
                                [System.StringComparison]::Ordinal)) {
                                throw "DNS registry state for $fam changed while it was being captured."
                            }
                            $source = if ([string]::IsNullOrWhiteSpace([string]$nameServerAfter)) {
                                'Automatic'
                            } else {
                                'Static'
                            }
                            "SOURCE_${fam}=$source"

                            foreach ($a in $dns.ServerAddresses) { "$fam=$a" }
                            "COMPLETE=$fam"
                        }

                        $adapterAfterCapture = Get-NetAdapter -InterfaceIndex $ifIndex -ErrorAction Stop
                        if ([Guid]($adapterAfterCapture.InterfaceGuid) -ne $interfaceGuid) {
                            throw 'The active network adapter changed while its DNS configuration was being captured.'
                        }
                        "IDENTITY=Verified"
                    }
                    finally {
                        $registry.Dispose()
                    }
                }
                """;

            Collection<PSObject> results = await _ps.RunAsync(script, cancellationToken: ct)
                .ConfigureAwait(false);

            List<string> v4 = [], v6 = [];
            int capturedIfIndex = 0;
            Guid? capturedInterfaceGuid = null;
            bool capturedV4 = false, capturedV6 = false;
            bool identityVerified = false;
            bool identityMetadataValid = true, sourceMetadataValid = true, addressMetadataValid = true;
            var v4Source = DnsConfigurationSource.Unknown;
            var v6Source = DnsConfigurationSource.Unknown;
            foreach (var line in results.Select(static result => result?.ToString()))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var tag = line[..eq];
                var value = line[(eq + 1)..];
                if (tag == "IFINDEX")
                {
                    int.TryParse(value, out capturedIfIndex);
                    continue;
                }
                if (tag == "IFGUID")
                {
                    if (Guid.TryParse(value, out var interfaceGuid))
                        capturedInterfaceGuid = interfaceGuid;
                    continue;
                }
                if (tag == "COMPLETE")
                {
                    if (value == "IPv4") capturedV4 = true;
                    else if (value == "IPv6") capturedV6 = true;
                    continue;
                }
                if (tag == "IDENTITY")
                {
                    if (identityVerified || value != "Verified")
                        identityMetadataValid = false;
                    else
                        identityVerified = true;
                    continue;
                }
                if (tag == "SOURCE_IPv4")
                {
                    if (v4Source != DnsConfigurationSource.Unknown ||
                        !TryParseConfigurationSource(value, out v4Source))
                    {
                        sourceMetadataValid = false;
                    }
                    continue;
                }
                if (tag == "SOURCE_IPv6")
                {
                    if (v6Source != DnsConfigurationSource.Unknown ||
                        !TryParseConfigurationSource(value, out v6Source))
                    {
                        sourceMetadataValid = false;
                    }
                    continue;
                }
                if (tag is "IPv4" or "IPv6")
                {
                    if (!IPAddress.TryParse(value, out var address) ||
                        (tag == "IPv4" && address.AddressFamily != AddressFamily.InterNetwork) ||
                        (tag == "IPv6" && address.AddressFamily != AddressFamily.InterNetworkV6))
                    {
                        addressMetadataValid = false;
                        continue;
                    }

                    if (tag == "IPv4") v4.Add(address.ToString());
                    else v6.Add(address.ToString());
                }
            }

            if (capturedIfIndex <= 0 ||
                capturedInterfaceGuid is null ||
                capturedInterfaceGuid == Guid.Empty)
            {
                throw new InvalidOperationException("No active network adapter could be captured.");
            }
            if (!capturedV4 ||
                !capturedV6 ||
                !identityVerified ||
                !identityMetadataValid ||
                !sourceMetadataValid ||
                !addressMetadataValid ||
                v4Source == DnsConfigurationSource.Unknown ||
                v6Source == DnsConfigurationSource.Unknown ||
                (v4Source == DnsConfigurationSource.Static && v4.Count == 0) ||
                (v6Source == DnsConfigurationSource.Static && v6.Count == 0))
            {
                throw new InvalidOperationException(
                    "The network adapter's DNS configuration could not be captured completely.");
            }

            return new DnsSnapshot(
                v4,
                v6,
                capturedIfIndex,
                capturedInterfaceGuid,
                v4Source,
                v6Source);
        }
        finally { _gate.Release(); }
    }

    private static bool TryParseConfigurationSource(
        string value,
        out DnsConfigurationSource source)
    {
        source = value switch
        {
            "Automatic" => DnsConfigurationSource.Automatic,
            "Static" => DnsConfigurationSource.Static,
            _ => DnsConfigurationSource.Unknown,
        };
        return source != DnsConfigurationSource.Unknown;
    }

    private static bool IsCapturedConfigurationSource(DnsConfigurationSource source) =>
        source is DnsConfigurationSource.Automatic or DnsConfigurationSource.Static;

    private static void ValidateCapturedTarget(DnsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.V4);
        ArgumentNullException.ThrowIfNull(snapshot.V6);
        if (snapshot.IfIndex <= 0 ||
            snapshot.InterfaceGuid is null ||
            snapshot.InterfaceGuid == Guid.Empty)
        {
            throw new InvalidOperationException(
                "The DNS operation has no valid captured network adapter.");
        }
        if (!IsCapturedConfigurationSource(snapshot.V4Source) ||
            !IsCapturedConfigurationSource(snapshot.V6Source))
        {
            throw new InvalidOperationException(
                "The DNS operation has no valid captured configuration source.");
        }

        ValidateSnapshotAddresses(snapshot);
    }

    private static DnsConfigurationSource ResolveConfigurationSource(
        DnsSnapshot snapshot,
        DnsConfigurationSource source,
        IReadOnlyList<string> addresses)
    {
        if (IsCapturedConfigurationSource(source))
            return source;

        if (snapshot.IfIndex == 0 && source == DnsConfigurationSource.Unknown)
        {
            return addresses.Count == 0
                ? DnsConfigurationSource.Automatic
                : DnsConfigurationSource.Static;
        }

        throw new InvalidOperationException(
            "The DNS snapshot has no valid captured configuration source.");
    }

    private static void ValidateSnapshotAddresses(DnsSnapshot snapshot)
    {
        ValidateAddressFamily(snapshot.V4, AddressFamily.InterNetwork, nameof(snapshot.V4));
        ValidateAddressFamily(snapshot.V6, AddressFamily.InterNetworkV6, nameof(snapshot.V6));
    }

    private static void ValidateAddressFamily(
        IReadOnlyList<string> addresses,
        AddressFamily expectedFamily,
        string parameterName)
    {
        foreach (var value in addresses)
        {
            if (!IPAddress.TryParse(value, out var address) ||
                address.AddressFamily != expectedFamily)
            {
                throw new ArgumentException(
                    $"Invalid DNS address in snapshot: '{value}'",
                    parameterName);
            }
        }
    }

    private static string FormatPowerShellAddresses(IReadOnlyList<string> addresses) =>
        string.Join(",", addresses.Select(static value => $"\"{IPAddress.Parse(value)}\""));

    private static void AppendCapturedAdapterLookup(
        System.Text.StringBuilder script,
        DnsSnapshot snapshot,
        bool allowIndexDrift)
    {
        ValidateCapturedTarget(snapshot);

        var expectedGuid = snapshot.InterfaceGuid!.Value;
        if (allowIndexDrift)
        {
            script.AppendLine(
                "$capturedAdapters = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | " +
                $"Where-Object {{ [Guid]$_.InterfaceGuid -eq [Guid]'{expectedGuid:D}' }})");
            script.AppendLine(
                "if ($capturedAdapters.Count -ne 1) " +
                "{ throw 'The network adapter captured before this DNS change is no longer present.' }");
            script.AppendLine("$capturedAdapter = $capturedAdapters[0]");
        }
        else
        {
            script.AppendLine(
                $"$capturedAdapter = Get-NetAdapter -InterfaceIndex {snapshot.IfIndex} -ErrorAction Stop");
            script.AppendLine(
                $"if ([Guid]($capturedAdapter.InterfaceGuid) -ne [Guid]'{expectedGuid:D}') " +
                "{ throw 'The network adapter captured before this DNS change is no longer present.' }");
        }

        script.AppendLine("$targetIfIndex = [int]$capturedAdapter.ifIndex");
    }

    private static void AppendExpectedDnsStateGuard(
        System.Text.StringBuilder script,
        DnsSnapshot snapshot)
    {
        AppendCapturedAdapterLookup(script, snapshot, allowIndexDrift: false);

        var expectedV4 = FormatPowerShellAddresses(snapshot.V4);
        var expectedV6 = FormatPowerShellAddresses(snapshot.V6);
        var expectedGuid = snapshot.InterfaceGuid!.Value;
        script.AppendLine(
            $"$expectedSources = @{{ IPv4 = '{snapshot.V4Source}'; IPv6 = '{snapshot.V6Source}' }}");
        script.AppendLine(
            $"$expectedAddresses = @{{ IPv4 = @({expectedV4}); IPv6 = @({expectedV6}) }}");
        script.AppendLine($"$guidKey = ([Guid]'{expectedGuid:D}').ToString('B')");
        script.AppendLine("$registryPaths = @{");
        script.AppendLine(
            "    IPv4 = \"SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\$guidKey\"");
        script.AppendLine(
            "    IPv6 = \"SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters\\Interfaces\\$guidKey\"");
        script.AppendLine("}");
        script.AppendLine(
            "$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey(" +
            "[Microsoft.Win32.RegistryHive]::LocalMachine, " +
            "[Microsoft.Win32.RegistryView]::Default)");
        script.AppendLine("try {");
        script.AppendLine("    foreach ($fam in @('IPv4','IPv6')) {");
        script.AppendLine("        $key = $registry.OpenSubKey($registryPaths[$fam], $false)");
        script.AppendLine(
            "        if ($null -eq $key) { throw \"DNS registry state for $fam is unavailable.\" }");
        script.AppendLine("        try {");
        script.AppendLine(
            "            $nameServerBefore = $key.GetValue(" +
            "'NameServer', $null, " +
            "[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)");
        script.AppendLine(
            "            if ($null -ne $nameServerBefore -and $nameServerBefore -isnot [string]) " +
            "{ throw \"DNS registry state for $fam has an unexpected value type.\" }");
        script.AppendLine(
            "            $dns = Get-DnsClientServerAddress " +
            "-InterfaceIndex $targetIfIndex -AddressFamily $fam -ErrorAction Stop");
        script.AppendLine(
            "            $nameServerAfter = $key.GetValue(" +
            "'NameServer', $null, " +
            "[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)");
        script.AppendLine(
            "            if ($null -ne $nameServerAfter -and $nameServerAfter -isnot [string]) " +
            "{ throw \"DNS registry state for $fam has an unexpected value type.\" }");
        script.AppendLine("        }");
        script.AppendLine("        finally { $key.Dispose() }");
        script.AppendLine(
            "        if (-not [string]::Equals(" +
            "[string]$nameServerBefore, [string]$nameServerAfter, " +
            "[System.StringComparison]::Ordinal)) " +
            "{ throw \"DNS registry state for $fam changed during validation.\" }");
        script.AppendLine("        # SYSMANAGER_DNS_STATE_COMPARISON_START");
        script.AppendLine(
            "        $actualSource = if ([string]::IsNullOrWhiteSpace([string]$nameServerAfter)) " +
            "{ 'Automatic' } else { 'Static' }");
        script.AppendLine(
            "        if ($actualSource -ne $expectedSources[$fam]) " +
            "{ throw \"DNS configuration source for $fam changed before mutation.\" }");
        script.AppendLine(
            "        $actual = @($dns.ServerAddresses | ForEach-Object " +
            "{ ([System.Net.IPAddress]::Parse([string]$_)).ToString() })");
        script.AppendLine("        $expected = @($expectedAddresses[$fam])");
        script.AppendLine(
            "        if ($actual.Count -ne $expected.Count) " +
            "{ throw \"DNS server addresses for $fam changed before mutation.\" }");
        script.AppendLine("        for ($i = 0; $i -lt $actual.Count; $i++) {");
        script.AppendLine(
            "            if (-not [string]::Equals(" +
            "$actual[$i], $expected[$i], [System.StringComparison]::OrdinalIgnoreCase)) " +
            "{ throw \"DNS server addresses for $fam changed before mutation.\" }");
        script.AppendLine("        }");
        script.AppendLine("        # SYSMANAGER_DNS_STATE_COMPARISON_END");
        script.AppendLine("    }");
        script.AppendLine("}");
        script.AppendLine("finally { $registry.Dispose() }");

        // Recheck identity after reading DNS state so the final guard and first mutation
        // remain adjacent in the same script.
        AppendCapturedAdapterLookup(script, snapshot, allowIndexDrift: false);
    }

    private static void AppendMutationPrecondition(
        System.Text.StringBuilder script,
        DnsSnapshot snapshot,
        bool allowIndexDrift,
        bool requireExpectedDnsState)
    {
        script.AppendLine("try {");
        if (requireExpectedDnsState)
            AppendExpectedDnsStateGuard(script, snapshot);
        else
            AppendCapturedAdapterLookup(script, snapshot, allowIndexDrift);
        script.AppendLine("}");
        script.AppendLine("catch {");
        script.AppendLine("    $failureReason = [string]$_.Exception.Message");
        script.AppendLine($"    \"{MutationPreconditionFailureMarker}|$failureReason\"");
        script.AppendLine("    return");
        script.AppendLine("}");
    }

    private static void ThrowIfMutationPreconditionFailed(Collection<PSObject> results)
    {
        var detailPrefix = MutationPreconditionFailureMarker + "|";
        foreach (var value in results.Select(static result => result?.ToString()))
        {
            if (string.Equals(value, MutationPreconditionFailureMarker, StringComparison.Ordinal))
                throw new DnsMutationPreconditionException();

            if (value?.StartsWith(detailPrefix, StringComparison.Ordinal) == true)
                throw new DnsMutationPreconditionException(value[detailPrefix.Length..]);
        }
    }

    /// <summary>
    /// Restores DNS to a previously captured IPv4-only set of server addresses. An empty
    /// snapshot means the adapter was on DHCP, so this resets to automatic.
    /// </summary>
    internal Task RestoreServersAsync(IReadOnlyList<string> servers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        return RestoreSnapshotAsync(new DnsSnapshot(servers, []), ct);
    }

    /// <summary>
    /// Restores DNS to a previously captured snapshot for BOTH families. Resets the adapter
    /// to DHCP first (clearing anything that was applied since, including filtering IPv6
    /// resolvers a v4-only restore would otherwise leave behind), then re-applies only the
    /// families captured as static. Effective DHCP addresses are retained for display but
    /// are never persisted as static overrides.
    /// </summary>
    public async Task RestoreSnapshotAsync(DnsSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.V4);
        ArgumentNullException.ThrowIfNull(snapshot.V6);

        var v4Source = ResolveConfigurationSource(snapshot, snapshot.V4Source, snapshot.V4);
        var v6Source = ResolveConfigurationSource(snapshot, snapshot.V6Source, snapshot.V6);
        if ((v4Source == DnsConfigurationSource.Static && snapshot.V4.Count == 0) ||
            (v6Source == DnsConfigurationSource.Static && snapshot.V6.Count == 0))
        {
            throw new InvalidOperationException(
                "A static DNS snapshot must include at least one server address.");
        }

        ValidateSnapshotAddresses(snapshot);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var script = new System.Text.StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");

            // Restore follows the stable GUID if Windows assigns that same adapter a new
            // interface index. Each later mutation resolves the GUID again so index reuse
            // cannot redirect a multi-step restore to another adapter.
            string targetIndex;
            if (snapshot.IfIndex > 0)
            {
                targetIndex = "$targetIfIndex";
                AppendMutationPrecondition(
                    script,
                    snapshot,
                    allowIndexDrift: true,
                    requireExpectedDnsState: false);
            }
            else
            {
                var ifIndex = await GetActiveInterfaceIndexAsync(ct).ConfigureAwait(false);
                targetIndex = ifIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            // Clear BOTH families first so any servers applied since (incl. IPv6) are removed.
            script.AppendLine(
                $"Set-DnsClientServerAddress -InterfaceIndex {targetIndex} " +
                "-ResetServerAddresses -ErrorAction Stop");
            if (v4Source == DnsConfigurationSource.Static)
            {
                if (snapshot.IfIndex > 0)
                    AppendCapturedAdapterLookup(script, snapshot, allowIndexDrift: true);
                var v4 = FormatPowerShellAddresses(snapshot.V4);
                script.AppendLine(
                    $"Set-DnsClientServerAddress -InterfaceIndex {targetIndex} " +
                    $"-ServerAddresses @({v4}) -ErrorAction Stop");
            }
            if (v6Source == DnsConfigurationSource.Static)
            {
                if (snapshot.IfIndex > 0)
                    AppendCapturedAdapterLookup(script, snapshot, allowIndexDrift: true);
                var v6 = FormatPowerShellAddresses(snapshot.V6);
                script.AppendLine(
                    $"Set-DnsClientServerAddress -InterfaceIndex {targetIndex} " +
                    $"-ServerAddresses @({v6}) -ErrorAction Stop");
            }

            Collection<PSObject> results = await _ps.RunAsync(
                    script.ToString(),
                    cancellationToken: ct)
                .ConfigureAwait(false);
            ThrowIfMutationPreconditionFailed(results);
        }
        finally { _gate.Release(); }
    }
    /// <summary>
    /// Sets the DNS server addresses on the active network adapter.
    /// </summary>
    internal Task SetDnsAsync(string primary, string secondary, CancellationToken ct = default) =>
        SetDnsCoreAsync(null, primary, secondary, "", "", ct);

    /// <summary>
    /// Sets the IPv4 DNS pair and, when supplied, the IPv6 pair on the active adapter.
    /// IPv6 is set as a separate address family so a v4-only preset leaves IPv6 untouched.
    /// All addresses are validated before any script runs.
    /// </summary>
    internal Task SetDnsAsync(
        string primary,
        string secondary,
        string primaryV6,
        string secondaryV6,
        CancellationToken ct = default) =>
        SetDnsCoreAsync(null, primary, secondary, primaryV6, secondaryV6, ct);

    /// <summary>
    /// Sets DNS on the exact adapter represented by <paramref name="target"/>. The adapter's
    /// stable identity is verified in the mutation script so topology changes cannot retarget
    /// an operation after its rollback snapshot was captured.
    /// </summary>
    public Task SetDnsAsync(
        DnsSnapshot target,
        string primary,
        string secondary,
        string primaryV6,
        string secondaryV6,
        CancellationToken ct = default)
    {
        ValidateCapturedTarget(target);
        return SetDnsCoreAsync(target, primary, secondary, primaryV6, secondaryV6, ct);
    }

    private async Task SetDnsCoreAsync(
        DnsSnapshot? target,
        string primary,
        string secondary,
        string primaryV6,
        string secondaryV6,
        CancellationToken ct)
    {
        if (!IPAddress.TryParse(primary, out var primaryAddress) ||
            primaryAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"Invalid primary DNS address: '{primary}'", nameof(primary));
        }
        if (!IPAddress.TryParse(secondary, out var secondaryAddress) ||
            secondaryAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"Invalid secondary DNS address: '{secondary}'", nameof(secondary));
        }

        var hasV6 = !string.IsNullOrEmpty(primaryV6);
        IPAddress? primaryV6Address = null;
        IPAddress? secondaryV6Address = null;
        if (hasV6)
        {
            if (!IPAddress.TryParse(primaryV6, out primaryV6Address) ||
                primaryV6Address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw new ArgumentException(
                    $"Invalid primary IPv6 DNS address: '{primaryV6}'",
                    nameof(primaryV6));
            }
            if (!string.IsNullOrEmpty(secondaryV6) &&
                (!IPAddress.TryParse(secondaryV6, out secondaryV6Address) ||
                 secondaryV6Address.AddressFamily != AddressFamily.InterNetworkV6))
            {
                throw new ArgumentException(
                    $"Invalid secondary IPv6 DNS address: '{secondaryV6}'",
                    nameof(secondaryV6));
            }
        }
        else if (!string.IsNullOrEmpty(secondaryV6))
        {
            throw new ArgumentException(
                "A secondary IPv6 DNS address requires a primary IPv6 address.",
                nameof(secondaryV6));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ifIndex = target?.IfIndex
                ?? await GetActiveInterfaceIndexAsync(ct).ConfigureAwait(false);
            string targetIndex = target is null
                ? ifIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "$targetIfIndex";

            // Validate identity and the confirmed DNS state in this same script. A
            // precondition rejection returns a typed no-mutation result; later failures
            // remain ambiguous so Undo stays armed for recovery.
            var script = new System.Text.StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            if (target is not null)
            {
                AppendMutationPrecondition(
                    script,
                    target,
                    allowIndexDrift: false,
                    requireExpectedDnsState: true);
            }
            script.AppendLine(
                $"Set-DnsClientServerAddress -InterfaceIndex {targetIndex} " +
                $"-ServerAddresses @(\"{primaryAddress}\",\"{secondaryAddress}\") " +
                "-ErrorAction Stop");
            if (hasV6)
            {
                if (target is not null)
                {
                    // IPv4 has already changed. Before touching IPv6, verify that IPv4
                    // still has the value just applied and that IPv6 still matches the
                    // confirmed snapshot. A concurrent writer therefore turns this into
                    // a recoverable partial failure instead of being silently overwritten.
                    var stateAfterIpv4 = target with
                    {
                        V4 = [primaryAddress.ToString(), secondaryAddress.ToString()],
                        V4Source = DnsConfigurationSource.Static,
                    };
                    AppendExpectedDnsStateGuard(script, stateAfterIpv4);
                }

                var v6List = secondaryV6Address is null
                    ? $"\"{primaryV6Address}\""
                    : $"\"{primaryV6Address}\",\"{secondaryV6Address}\"";
                // IPv4 has already changed if this second mutation fails. Let the error
                // propagate so the caller reports a partial failure and keeps Undo available.
                script.AppendLine(
                    $"Set-DnsClientServerAddress -InterfaceIndex {targetIndex} " +
                    $"-ServerAddresses @({v6List}) -ErrorAction Stop");
            }

            Collection<PSObject> results = await _ps.RunAsync(
                    script.ToString(),
                    cancellationToken: ct)
                .ConfigureAwait(false);
            ThrowIfMutationPreconditionFailed(results);
        }
        finally { _gate.Release(); }
    }
    /// <summary>
    /// Resets DNS to automatic (DHCP) on the active network adapter.
    /// </summary>
    internal Task ResetToDhcpAsync(CancellationToken ct = default) =>
        ResetToDhcpCoreAsync(null, ct);

    /// <summary>
    /// Resets DNS to DHCP on the exact adapter represented by <paramref name="target"/>.
    /// </summary>
    public Task ResetToDhcpAsync(DnsSnapshot target, CancellationToken ct = default)
    {
        ValidateCapturedTarget(target);
        return ResetToDhcpCoreAsync(target, ct);
    }

    private async Task ResetToDhcpCoreAsync(DnsSnapshot? target, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ifIndex = target?.IfIndex
                ?? await GetActiveInterfaceIndexAsync(ct).ConfigureAwait(false);
            string targetIndex = target is null
                ? ifIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "$targetIfIndex";

            var script = new System.Text.StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            if (target is not null)
            {
                AppendMutationPrecondition(
                    script,
                    target,
                    allowIndexDrift: false,
                    requireExpectedDnsState: true);
            }
            script.AppendLine(
                $"Set-DnsClientServerAddress -InterfaceIndex {targetIndex} " +
                "-ResetServerAddresses -ErrorAction Stop");

            Collection<PSObject> results = await _ps.RunAsync(
                    script.ToString(),
                    cancellationToken: ct)
                .ConfigureAwait(false);
            ThrowIfMutationPreconditionFailed(results);
        }
        finally { _gate.Release(); }
    }
}
