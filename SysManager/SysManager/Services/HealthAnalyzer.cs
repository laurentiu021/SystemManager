// SysManager · HealthAnalyzer
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Pure function that turns a bag of per-target metrics into a single
/// health verdict. Kept free of UI/threading concerns so it's trivially
/// testable.
///
/// Heuristic (running from closest-to-user to farthest):
///   1. Local gateway bad → LocalNetwork
///   2. Public DNS bad, nothing farther bad → IspOrUpstream
///   3. Public DNS and a far end bad → Mixed
///   4. One far end bad (game servers, streaming, or the user's own targets) → GameServer,
///      StreamingService or RemoteTarget, when a public DNS reference answers cleanly;
///      Inconclusive when none was measured
///   5. More than one far end bad → Mixed
///   6. All clean → Good
/// A layer with nothing measured in it is not called clean, and the detail names what
/// tripped: loss, jitter or latency (#2712).
/// </summary>
public static class HealthAnalyzer
{
    // Thresholds that flag a target as "in trouble". Tuned conservatively
    // so transient hiccups don't raise false alarms.
    public const double LossWarnPercent = 2.0;
    public const double JitterWarnMs = 20.0;
    public const double PingWarnGatewayMs = 15.0;
    public const double PingWarnDnsMs = 60.0;

    public readonly record struct TargetMetric(
        string Name,
        TargetRole Role,
        double? AverageMs,
        double? JitterMs,
        double LossPercent,
        int SampleCount);

    public static HealthDiagnostic Analyze(IEnumerable<TargetMetric> metrics)
    {
        var list = metrics.Where(m => m.SampleCount > 0).ToList();
        var diag = new HealthDiagnostic();

        if (list.Count == 0)
        {
            diag.Verdict = HealthVerdict.Unknown;
            diag.Headline = "No samples yet";
            diag.Detail = "Press Start and wait a few seconds.";
            diag.ColorHex = StatusColors.Neutral;
            return diag;
        }

        // Overall numbers for the pills.
        diag.WorstLossPercent = list.Max(m => m.LossPercent);
        diag.WorstJitterMs = list.Max(m => m.JitterMs ?? 0);
        var pings = list.Where(m => m.AverageMs.HasValue).Select(m => m.AverageMs!.Value).ToList();
        diag.AveragePingMs = pings.Count > 0 ? pings.Average() : 0;

        // What tripped in each layer, or null when nothing in it did. A layer with no targets in it was not measured,
        // which is not clean: the game presets had no DNS reference, and the verdict called DNS and the gateway clean.
        var gateway = Trouble(list, TargetRole.Gateway, PingWarnGatewayMs);
        var dns = Trouble(list, TargetRole.PublicDns, PingWarnDnsMs);
        bool hasGateway = list.Any(m => m.Role == TargetRole.Gateway);
        bool hasDns = list.Any(m => m.Role == TargetRole.PublicDns);

        // The far ends. Generic targets are one of them, google.com and every target the user adds: the verdict read
        // none of them, while their loss was in the worst-loss pill, so a lossy target of one's own read "healthy".
        var game = Trouble(list, TargetRole.GameServer);
        var stream = Trouble(list, TargetRole.Streaming);
        var other = Trouble(list, TargetRole.Generic);
        int farBad = new[] { game, stream, other }.Count(t => t is not null);

        if (gateway is null && dns is null && farBad == 0)
        {
            diag.Verdict = HealthVerdict.Good;
            diag.Headline = "Connection is healthy";
            diag.Detail = string.Create(CultureInfo.InvariantCulture, $"Avg {diag.AveragePingMs:F0} ms · {diag.WorstLossPercent:F1}% worst loss · {diag.WorstJitterMs:F0} ms jitter.");
            diag.ColorHex = StatusColors.Good;
            return diag;
        }

        if (gateway is not null)
        {
            diag.Verdict = HealthVerdict.LocalNetwork;
            diag.Headline = "Problem on your local network";
            diag.Detail = $"Your router/gateway is showing {gateway}. Check Wi-Fi signal, restart the router, or try cable.";
            diag.ColorHex = StatusColors.Bad;
            return diag;
        }

        var (farName, farTrouble) = game is not null ? ("the game server", game)
            : stream is not null ? ("the streaming services", stream)
            : (OwnTargetName(list), other);

        if (dns is not null && farBad == 0)
        {
            diag.Verdict = HealthVerdict.IspOrUpstream;
            diag.Headline = "Problem at your ISP or upstream";
            diag.Detail = hasGateway
                ? $"Gateway is clean but public DNS is showing {dns}. Contact your ISP if this persists."
                : $"Public DNS is showing {dns}. Your router was not measured, so the problem may also be on your local "
                  + "network. Contact your ISP if this persists.";
            diag.ColorHex = StatusColors.Warning;
            return diag;
        }

        if (dns is not null || farBad > 1)
        {
            diag.Verdict = HealthVerdict.Mixed;
            diag.Headline = "Multiple layers affected";
            diag.Detail = dns is not null && farBad == 1
                ? $"Both public DNS and {farName} are showing trouble, likely an ISP or routing issue affecting more than "
                  + "one place."
                : $"{farBad + (dns is null ? 0 : 1)} network layers are showing trouble. Check the per-target stats to "
                  + "localize.";
            diag.ColorHex = StatusColors.Bad;
            return diag;
        }

        // One far end is in trouble, and public DNS is not. Without a DNS reference, the far end and the provider in
        // between cannot be told apart.
        if (!hasDns)
        {
            diag.Verdict = HealthVerdict.Inconclusive;
            diag.Headline = $"Trouble reaching {farName}";
            diag.Detail = $"No public DNS server was measured, so this cannot tell whether the {farTrouble} is at "
                + $"{farName} or at your provider.";
            diag.ColorHex = StatusColors.Warning;
            return diag;
        }

        var clean = hasGateway
            ? "Your connection to DNS and gateway is clean"
            : "Public DNS answers cleanly, and your router was not measured";
        diag.ColorHex = StatusColors.Info;
        if (game is not null)
        {
            diag.Verdict = HealthVerdict.GameServer;
            diag.Headline = "It's the game server, not you";
            diag.Detail = $"{clean}; only the game server is showing {game}. Try another region or wait it out.";
        }
        else if (stream is not null)
        {
            diag.Verdict = HealthVerdict.StreamingService;
            diag.Headline = "Streaming service is slow";
            diag.Detail = $"{clean}; only the streaming services are showing {stream}, likely a CDN issue, not your connection.";
        }
        else
        {
            diag.Verdict = HealthVerdict.RemoteTarget;
            diag.Headline = $"It's {farName}, not you";
            diag.Detail = $"{clean}; only {farName} is showing {farTrouble}.";
        }
        return diag;
    }

    /// <summary>What made a target bad, the worst first, or null when it is fine.</summary>
    private static string? Reason(TargetMetric m, double? pingWarn = null)
    {
        if (m.LossPercent >= LossWarnPercent) return "packet loss";
        if ((m.JitterMs ?? 0) >= JitterWarnMs) return "jitter";
        if (pingWarn.HasValue && (m.AverageMs ?? 0) >= pingWarn.Value) return "high latency";
        return null;
    }

    /// <summary>
    /// What made a layer bad, as the worst of its targets' reasons, or null when none of them is bad. The detail used
    /// to say "loss" whatever tripped, so a DNS server answering every ping at 65 ms was reported as losing packets.
    /// </summary>
    private static string? Trouble(IReadOnlyList<TargetMetric> list, TargetRole role, double? pingWarn = null)
    {
        var reasons = list.Where(m => m.Role == role).Select(m => Reason(m, pingWarn)).ToList();
        return reasons.Contains("packet loss") ? "packet loss"
            : reasons.Contains("jitter") ? "jitter"
            : reasons.Contains("high latency") ? "high latency"
            : null;
    }

    // The user's own target in trouble by its name, or several of them together.
    private static string OwnTargetName(IReadOnlyList<TargetMetric> list)
    {
        var bad = list.Where(m => m.Role == TargetRole.Generic && Reason(m) is not null).ToList();
        return bad.Count == 1 ? bad[0].Name : "those targets";
    }
}
