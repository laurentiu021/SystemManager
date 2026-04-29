// SysManager · TargetPresetTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class TargetPresetTests
{
    [Fact]
    public void All_ContainsAllKnownPresets()
    {
        Assert.Contains(TargetPresets.Global, TargetPresets.All);
        Assert.Contains(TargetPresets.CS2Europe, TargetPresets.All);
        Assert.Contains(TargetPresets.FaceitEurope, TargetPresets.All);
        Assert.Contains(TargetPresets.PubgEurope, TargetPresets.All);
        Assert.Contains(TargetPresets.Streaming, TargetPresets.All);
    }

    [Fact]
    public void All_HasAtLeastFivePresets()
    {
        Assert.True(TargetPresets.All.Count >= 5);
    }

    [Fact]
    public void AllPresets_HaveNonEmptyNameAndDescription()
    {
        foreach (var p in TargetPresets.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name), $"Preset name empty for {p}");
            Assert.False(string.IsNullOrWhiteSpace(p.Description), $"Description empty: {p.Name}");
        }
    }

    [Fact]
    public void AllPresets_HaveAtLeastOneTarget()
    {
        foreach (var p in TargetPresets.All)
            Assert.NotEmpty(p.Targets);
    }

    [Fact]
    public void AllPresets_HaveUniqueTargets()
    {
        foreach (var p in TargetPresets.All)
        {
            var hosts = p.Targets.Select(t => t.Host).ToList();
            Assert.Equal(hosts.Count, hosts.Distinct().Count());
        }
    }

    [Fact]
    public void AllTargets_HaveNonEmptyNameAndHost()
    {
        foreach (var p in TargetPresets.All)
            foreach (var (name, host) in p.Targets)
            {
                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.False(string.IsNullOrWhiteSpace(host));
            }
    }

    [Fact]
    public void Global_HasMultipleDnsProviders()
    {
        Assert.Contains(TargetPresets.Global.Targets, t => t.Host == "8.8.8.8");
        Assert.Contains(TargetPresets.Global.Targets, t => t.Host == "1.1.1.1");
        Assert.Contains(TargetPresets.Global.Targets, t => t.Host == "9.9.9.9");
    }

    [Fact]
    public void CS2Europe_CoversMainRegions()
    {
        var hosts = TargetPresets.CS2Europe.Targets.Select(t => t.Host).ToList();
        // Known Valve EU subnets — at least one IP from each
        Assert.Contains(hosts, h => h.StartsWith("146.66.155"));  // Vienna
        Assert.Contains(hosts, h => h.StartsWith("146.66.152"));  // Luxembourg
        Assert.Contains(hosts, h => h.StartsWith("155.133."));    // Poland / EU
        Assert.Contains(hosts, h => h.StartsWith("162.254."));    // EU Central
    }

    [Fact]
    public void PubgEurope_UsesAwsEndpoints()
    {
        Assert.All(TargetPresets.PubgEurope.Targets, t =>
            Assert.Contains("amazonaws", t.Host));
    }

    [Fact]
    public void Streaming_IncludesYoutubeAndTwitch()
    {
        var hosts = TargetPresets.Streaming.Targets.Select(t => t.Host).ToList();
        Assert.Contains("youtube.com", hosts);
        Assert.Contains("twitch.tv", hosts);
    }

    [Fact]
    public void PresetNames_AreUnique()
    {
        var names = TargetPresets.All.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void CS2EuropeIPs_AreValidIPv4()
    {
        foreach (var (_, host) in TargetPresets.CS2Europe.Targets)
        {
            Assert.True(System.Net.IPAddress.TryParse(host, out var ip),
                $"CS2 Europe target {host} is not a valid IP");
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, ip.AddressFamily);
        }
    }

    [Fact]
    public void PresetEquality_UsesReferenceForTargetsArray()
    {
        // Record equality on IReadOnlyList compares references, not contents.
        // Two presets with identical data but different list instances are NOT equal.
        var arr = new (string, string)[] { ("n", "h") };
        var a = new TargetPreset("x", "d", arr);
        var b = new TargetPreset("x", "d", arr);
        // Same list reference => equal.
        Assert.Equal(a, b);

        var c = new TargetPreset("x", "d", new (string, string)[] { ("n", "h") });
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void FaceitEurope_CoversMainCountries()
    {
        var targets = TargetPresets.FaceitEurope.Targets;
        Assert.True(targets.Count >= 5, $"Expected at least 5 FACEIT targets, got {targets.Count}");
        var names = targets.Select(t => t.Name).ToList();
        Assert.Contains(names, n => n.StartsWith("FACEIT DE"));
        Assert.Contains(names, n => n.StartsWith("FACEIT UK"));
        Assert.Contains(names, n => n.StartsWith("FACEIT NL"));
        // Germany has multiple IPs
        Assert.True(names.Count(n => n.StartsWith("FACEIT DE")) >= 2,
            "Expected at least 2 FACEIT DE targets");
    }

    [Fact]
    public void FaceitEuropeIPs_AreValidIPv4()
    {
        foreach (var (_, host) in TargetPresets.FaceitEurope.Targets)
        {
            Assert.True(System.Net.IPAddress.TryParse(host, out var ip),
                $"FACEIT Europe target {host} is not a valid IP");
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, ip.AddressFamily);
        }
    }
}
