// SysManager · DnsPreset
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Represents a DNS server preset (e.g. Google, Cloudflare, Quad9).
/// </summary>
public sealed class DnsPreset
{
    public required string Name { get; init; }
    public required string Primary { get; init; }
    public required string Secondary { get; init; }
    public string Description { get; init; } = "";

    public override string ToString() => Name;
}
