// SysManager · DnsPreset
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Represents a DNS server preset (e.g. Google, Cloudflare, Quad9), including optional
/// filtering variants (ad/malware/family blocking) and IPv6 resolvers configured
/// alongside the IPv4 pair when the provider offers them.
/// </summary>
public sealed class DnsPreset
{
    public required string Name { get; init; }
    public required string Primary { get; init; }
    public required string Secondary { get; init; }
    public string Description { get; init; } = "";

    /// <summary>Optional primary IPv6 resolver, set alongside IPv4 when non-empty.</summary>
    public string PrimaryV6 { get; init; } = "";

    /// <summary>Optional secondary IPv6 resolver.</summary>
    public string SecondaryV6 { get; init; } = "";

    /// <summary>True when this preset configures IPv6 resolvers in addition to IPv4.</summary>
    public bool HasIpv6 => !string.IsNullOrEmpty(PrimaryV6);

    public override string ToString() => Name;
}
