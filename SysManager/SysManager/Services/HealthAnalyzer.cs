using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Pure function that turns a bag of per-target metrics into a single
/// health verdict. Kept free of UI/threading concerns so it's trivially
/// testable.
///
/// Heuristic (running from closest-to-user to farthest):
///   1. Local gateway bad → LocalNetwork
///   2. Public DNS bad → IspOrUpstream
///   3. Only game targets bad → GameServer
///   4. Only streaming targets bad → StreamingService
///   5. Multiple layers bad → Mixed
///   6. All clean → Good
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
            diag.ColorHex = "#9AA0A6";
            return diag;
        }

        // Overall numbers for the pills.
        diag.WorstLossPercent = list.Max(m => m.LossPercent);
        diag.WorstJitterMs = list.Max(m => m.JitterMs ?? 0);
        var pings = list.Where(m => m.AverageMs.HasValue).Select(m => m.AverageMs!.Value).ToList();
        diag.AveragePingMs = pings.Count > 0 ? pings.Average() : 0;

        bool gatewayBad = list.Any(m => m.Role == TargetRole.Gateway && IsBad(m, PingWarnGatewayMs));
        bool dnsBad = list.Any(m => m.Role == TargetRole.PublicDns && IsBad(m, PingWarnDnsMs));
        bool gameBad = list.Any(m => m.Role == TargetRole.GameServer && IsBad(m));
        bool streamBad = list.Any(m => m.Role == TargetRole.Streaming && IsBad(m));

        var layersBad = new[] { gatewayBad, dnsBad, gameBad, streamBad }.Count(x => x);

        if (!gatewayBad && !dnsBad && !gameBad && !streamBad)
        {
            diag.Verdict = HealthVerdict.Good;
            diag.Headline = "Connection is healthy";
            diag.Detail = $"Avg {diag.AveragePingMs:F0} ms · {diag.WorstLossPercent:F1}% worst loss · {diag.WorstJitterMs:F0} ms jitter.";
            diag.ColorHex = "#06D6A0";
            return diag;
        }

        if (gatewayBad)
        {
            diag.Verdict = HealthVerdict.LocalNetwork;
            diag.Headline = "Problem on your local network";
            diag.Detail = "Your router/gateway is showing loss or high latency. Check Wi-Fi signal, restart the router, or try cable.";
            diag.ColorHex = "#FF6B6B";
            return diag;
        }

        if (dnsBad && !gameBad && !streamBad)
        {
            diag.Verdict = HealthVerdict.IspOrUpstream;
            diag.Headline = "Problem at your ISP or upstream";
            diag.Detail = "Gateway is clean but public DNS is showing loss. Contact your ISP if this persists; the game server is probably fine.";
            diag.ColorHex = "#FFD166";
            return diag;
        }

        if (gameBad)
        {
            diag.Verdict = HealthVerdict.GameServer;
            diag.Headline = "It's the game server, not you";
            diag.Detail = "Your connection to DNS and gateway is clean — only the game server is showing loss/jitter. Try another region or wait it out.";
            diag.ColorHex = "#F72585";
            return diag;
        }

        if (streamBad && !dnsBad)
        {
            diag.Verdict = HealthVerdict.StreamingService;
            diag.Headline = "Streaming service is slow";
            diag.Detail = "Only streaming endpoints (YouTube/Twitch) are showing trouble — likely a CDN issue, not your connection.";
            diag.ColorHex = "#B388FF";
            return diag;
        }

        diag.Verdict = HealthVerdict.Mixed;
        diag.Headline = "Multiple layers affected";
        diag.Detail = $"{layersBad} network layers are showing trouble. Check the per-target stats to localize.";
        diag.ColorHex = "#FF6B6B";
        return diag;
    }

    private static bool IsBad(TargetMetric m, double? pingWarn = null)
    {
        if (m.LossPercent >= LossWarnPercent) return true;
        if ((m.JitterMs ?? 0) >= JitterWarnMs) return true;
        if (pingWarn.HasValue && (m.AverageMs ?? 0) >= pingWarn.Value) return true;
        return false;
    }
}
