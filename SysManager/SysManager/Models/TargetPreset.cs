// SysManager · TargetPreset
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A named set of ping targets. Switching presets replaces the current
/// target list (keeping the local gateway and any custom-added targets).
/// </summary>
public sealed record TargetPreset(
    string Name,
    string Description,
    IReadOnlyList<(string Name, string Host)> Targets);

public static class TargetPresets
{
    public static readonly TargetPreset Global = new(
        "Global",
        "General connectivity: DNS servers, gateway, a common domain.",
        new (string, string)[]
        {
            ("Google DNS",  "8.8.8.8"),
            ("Cloudflare",  "1.1.1.1"),
            ("Quad9",       "9.9.9.9"),
            ("google.com",  "google.com"),
        });

    public static readonly TargetPreset CS2Europe = new(
        "CS2 Europe",
        "Representative IPs from Valve's EU matchmaking subnets.",
        new (string, string)[]
        {
            ("CS2 Vienna",     "146.66.155.1"),
            ("CS2 Stockholm",  "146.66.156.1"),
            ("CS2 Luxembourg", "146.66.152.1"),
            ("CS2 Warsaw",     "155.133.240.1"),
        });

    public static readonly TargetPreset PubgEurope = new(
        "PUBG Europe",
        "AWS regions PUBG uses for EU matches.",
        new (string, string)[]
        {
            ("AWS Frankfurt", "ec2.eu-central-1.amazonaws.com"),
            ("AWS Ireland",   "ec2.eu-west-1.amazonaws.com"),
            ("AWS London",    "ec2.eu-west-2.amazonaws.com"),
        });

    public static readonly TargetPreset Streaming = new(
        "Streaming",
        "Video services — useful to correlate buffering with network issues.",
        new (string, string)[]
        {
            ("YouTube",    "youtube.com"),
            ("Twitch",     "twitch.tv"),
            ("Cloudflare", "cloudflare.com"),
        });

    public static readonly TargetPreset FaceitEurope = new(
        "FACEIT Europe",
        "FACEIT CS2 competitive servers across EU — Germany, UK, France, Netherlands, Sweden.",
        new (string, string)[]
        {
            ("FACEIT DE",  "88.198.52.17"),
            ("FACEIT UK",  "82.145.38.1"),
            ("FACEIT FR",  "62.210.84.97"),
            ("FACEIT NL",  "46.166.189.17"),
            ("FACEIT SE",  "31.3.153.1"),
        });

    public static readonly IReadOnlyList<TargetPreset> All = new[]
    {
        Global, CS2Europe, FaceitEurope, PubgEurope, Streaming
    };
}
