// SysManager · SystemReportService — generates a comprehensive system report (text/HTML/JSON)
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Management;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Generates a full system report combining existing service data (OS, CPU, RAM,
/// Disks, SMART) with additional WMI queries (GPU, Motherboard, Network adapters).
/// The data is gathered once into a <see cref="SystemReportData"/> and rendered to
/// plain text, HTML, or JSON so all three formats share a single source of truth.
/// It opens with a health verdict — the Dashboard's score and recommendations, and the
/// recent problems Windows logged — so a reader learns what is wrong before the
/// inventory (#1508).
/// </summary>
public sealed class SystemReportService
{
    /// <summary>How far back the report looks for problems in the System log.</summary>
    internal static readonly TimeSpan RecentProblemsWindow = TimeSpan.FromDays(7);

    /// <summary>How many kinds of problem the report lists at most.</summary>
    internal const int MaxRecentProblems = 5;

    /// <summary>
    /// The longest the report waits on the System log. A read walks every record in the window, thousands on a
    /// busy machine, and the report is worth more without its problems list than not at all.
    /// </summary>
    internal static readonly TimeSpan RecentProblemsBudget = TimeSpan.FromSeconds(15);

    private readonly SystemInfoService _sysInfo;
    private readonly DiskHealthService _diskHealth;
    private readonly BatteryService _battery;
    private readonly EventLogService _events;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // The report is written to a local file the user chooses; relax escaping so
        // names with '+'/'&' (e.g. "Notepad++") read naturally rather than as \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public SystemReportService(
        SystemInfoService sysInfo, DiskHealthService diskHealth, BatteryService battery, EventLogService events)
    {
        _sysInfo = sysInfo;
        _diskHealth = diskHealth;
        _battery = battery;
        _events = events;
    }

    /// <summary>
    /// Gathers the full report payload (health, OS/CPU/RAM/GPU/motherboard/disks/network) once.
    /// </summary>
    /// <remarks>
    /// <para><b>The machine identifiers are removed here, at the one point every format passes through.</b>
    /// Redacting in each renderer instead would mean three places to remember and a fourth format shipping
    /// unprotected — and the report already had exactly that shape: the sharable text variant was redacted
    /// while the text, HTML and JSON exports were not (#2352).</para>
    /// <para>The health score is computed from the snapshot and disks gathered here, through
    /// <see cref="HealthScoreService.Evaluate"/>, rather than by asking Windows for both again. The recent
    /// problems carry no event message (<see cref="SummarizeProblems"/>), so the health section adds nothing
    /// the redaction has not seen.</para>
    /// </remarks>
    public async Task<SystemReportData> GenerateDataAsync(CancellationToken ct = default)
    {
        var snapshot = await _sysInfo.CaptureAsync(ct).ConfigureAwait(false);
        var diskHealth = await _diskHealth.CollectAsync(ct).ConfigureAwait(false);
        var battery = await ReadBatteryAsync(ct).ConfigureAwait(false);
        var problems = await ReadRecentProblemsAsync(ct).ConfigureAwait(false);

        var data = await Task.Run(() => BuildData(snapshot, diskHealth), ct).ConfigureAwait(false);
        var score = HealthScoreService.Evaluate(snapshot, diskHealth, battery,
            FixedDriveService.Enumerate(), HealthScoreService.SystemDriveLetter());
        return WithoutMachineIdentifiers(data with { Health = BuildHealth(score, problems) });
    }

    /// <summary>The battery for the score, or null when Windows would not say; the score then leaves it out.</summary>
    private async Task<BatteryInfo?> ReadBatteryAsync(CancellationToken ct)
    {
        try { return await _battery.GetBatteryInfoAsync(ct).ConfigureAwait(false); }
        catch (ManagementException ex) { Log.Debug("Report: battery unavailable: {Error}", ex.Message); }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Debug("Report: battery WMI COM error: 0x{HResult:X8}", ex.HResult); }
        catch (InvalidOperationException ex) { Log.Debug("Report: battery unavailable: {Error}", ex.Message); }
        return null;
    }

    /// <summary>
    /// The recent critical and error events from the System log, summarized; null when the log could not be
    /// read in full, so the report says that instead of claiming there were none.
    /// </summary>
    private async Task<IReadOnlyList<ReportProblem>?> ReadRecentProblemsAsync(CancellationToken ct)
    {
        var options = new EventLogQueryOptions
        {
            LogName = "System",
            Severities = [EventSeverity.Critical, EventSeverity.Error],
            Since = DateTime.Now - RecentProblemsWindow,
            MaxResults = 500,
        };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RecentProblemsBudget);

        List<FriendlyEventEntry> entries = [];
        try
        {
            await foreach (var entry in _events.ReadAsync(options, budget.Token).ConfigureAwait(false))
                entries.Add(entry);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning("Report: the System log took longer than {Budget} to read, so the report lists no problems",
                RecentProblemsBudget);
            return null;
        }

        if (_events.LastOutcome != EventLogService.ReadOutcome.Ok)
        {
            Log.Debug("Report: the System log could not be read: {Outcome}", _events.LastOutcome);
            return null;
        }
        return SummarizeProblems(entries);
    }

    /// <summary>
    /// The events grouped by source and ID: how many times each was logged and when last, critical first, then
    /// newest first, at most <see cref="MaxRecentProblems"/>.
    /// </summary>
    /// <remarks>
    /// Grouped because one noisy source can log the same error hundreds of times a week, and a list of the five
    /// newest events would then be that one error five times. Only the written explanation and advice travel:
    /// an event's message can name files, users and machines, which the report keeps out (#2352), and the
    /// fallback wording for an unknown event points at that message. An event nobody wrote an explanation for
    /// is listed by its source and ID alone, which is what to search for.
    /// </remarks>
    internal static IReadOnlyList<ReportProblem> SummarizeProblems(IEnumerable<FriendlyEventEntry> entries) =>
    [
        .. entries
            .GroupBy(e => (e.ProviderName, e.EventId))
            .Select(g =>
            {
                var latest = g.MaxBy(e => e.Timestamp)!;
                var written = EventExplainer.TryExplain(latest.ProviderName, latest.EventId, out var known);
                return new ReportProblem(latest.Timestamp, latest.ProviderName, latest.EventId,
                    Critical: g.Any(e => e.Severity == EventSeverity.Critical), Count: g.Count(),
                    written ? known.Explanation : null, written ? known.Recommendation : null);
            })
            .OrderByDescending(p => p.Critical)
            .ThenByDescending(p => p.LastSeen)
            .Take(MaxRecentProblems),
    ];

    /// <summary>
    /// The health section from the score and the problems: the components in the Dashboard's order, with a
    /// component Windows gave nothing for marked as unread rather than scored at the fallback.
    /// </summary>
    internal static ReportHealth BuildHealth(HealthScoreResult score, IReadOnlyList<ReportProblem>? problems)
    {
        int? Read(string component, int value) => score.IsUnavailable(component) ? null : value;

        List<ReportHealthComponent> components =
        [
            new("Disk health", Read(HealthScoreService.DiskComponent, score.DiskScore)),
            new("Free space", Read(HealthScoreService.FreeSpaceComponent, score.FreeSpaceScore)),
            new("Memory", Read(HealthScoreService.MemoryComponent, score.RamScore)),
            new("Uptime", Read(HealthScoreService.UptimeComponent, score.UptimeScore)),
        ];
        // A PC with no battery has nothing to list; one Windows would not measure is listed as unread.
        if (score.HasBattery)
            components.Add(new("Battery", Read(HealthScoreService.BatteryComponent, score.BatteryScore)));

        return new ReportHealth(
            score.Score,
            score.Label,
            components,
            [.. score.Recommendations.Select(r => new ReportRecommendation(r.Message, r.Severity))],
            problems ?? [],
            ProblemsRead: problems is not null);
    }

    /// <summary>Generates a formatted plain-text system report.</summary>
    public async Task<string> GenerateReportAsync(CancellationToken ct = default)
        => BuildText(await GenerateDataAsync(ct).ConfigureAwait(false));

    /// <summary>A copy of the payload with each adapter's MAC dropped and its IPv4 host part masked.</summary>
    /// <remarks>
    /// A MAC address is permanent: it survives a Windows reinstall, and nobody can un-publish it once it is in
    /// a GitHub thread. Everything else in the report is generic hardware or transient state, and there is no
    /// user name or machine name anywhere in it — those were handled carefully. These two fields were
    /// different in kind, and the report is the app's own answer to "produce evidence for a bug": SUPPORT.md,
    /// the issue template and the About tab's "Report a problem" button all funnel someone toward attaching
    /// one, and the target persona attaches what the app hands them without reading it (#2352).
    /// <para>The network half of the IPv4 is kept on purpose. "Is there a private address, a static one, or a
    /// <c>169.254</c> self-assigned one" is the actual signal in a report attached to a "no internet"
    /// complaint; the host number answers nothing anyone has ever asked.</para>
    /// <para>Redacted from the DATA rather than by pattern-matching the rendered text, which is the only way
    /// to be certain what is being replaced. A generic IPv4 regex over the finished report would also match a
    /// four-part version string — <c>1.112.0.0</c> has four valid octets — so it would have quietly corrupted
    /// the version line while claiming to protect an address.</para>
    /// <para><b>No full-value variant is offered.</b> Three options were weighed: redact everywhere, keep the
    /// values and disclose them, or ship two reports. Disclosure puts the judgement on the person least
    /// equipped to make it at the moment they are already frustrated, and two variants need two names a
    /// non-technical user can tell apart, which is where that idea fails. The deciding argument is that the
    /// full values answer no question a bug report asks — so this is not privacy against diagnostics, it is
    /// privacy against a field nobody uses. The MAC appears nowhere else in the app, so nothing else lost a
    /// capability.</para>
    /// <para>It also covers the vector the options did not mention: the on-screen report is the same text,
    /// so a screenshot of the System Report tab used to publish the MAC just as an export did.</para>
    /// </remarks>
    internal static SystemReportData WithoutMachineIdentifiers(SystemReportData d) =>
        d with
        {
            NetworkAdapters =
            [
                .. d.NetworkAdapters.Select(n => n with
                {
                    IPv4 = MaskHostPart(n.IPv4),
                    // A placeholder rather than an empty string, so a reader can tell "removed on purpose"
                    // from "the adapter did not report one" — the text builder prints this field either way.
                    MacAddress = n.MacAddress.Length == 0 ? "" : "(not included)",
                })
            ],
        };

    /// <summary>
    /// <c>192.168.1.42</c> becomes <c>192.168.x.x</c>. Anything that is not four numeric parts is returned
    /// unchanged rather than guessed at.
    /// </summary>
    internal static string MaskHostPart(string ipv4)
    {
        if (ipv4.Length == 0) return ipv4;

        var parts = ipv4.Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return ipv4;

        return $"{parts[0]}.{parts[1]}.x.x";
    }

    /// <summary>Generates a self-contained, styled HTML system report.</summary>
    public async Task<string> GenerateHtmlAsync(CancellationToken ct = default)
        => BuildHtml(await GenerateDataAsync(ct).ConfigureAwait(false));

    /// <summary>Generates a structured JSON system report.</summary>
    public async Task<string> GenerateJsonAsync(CancellationToken ct = default)
        => BuildJson(await GenerateDataAsync(ct).ConfigureAwait(false));

    /// <summary>Serializes the report payload to indented JSON.</summary>
    internal static string BuildJson(SystemReportData d) => JsonSerializer.Serialize(d, JsonOptions);

    // ── Data gathering ───────────────────────────────────────────────────────

    private static SystemReportData BuildData(SystemSnapshot snapshot, IReadOnlyList<DiskHealthReport> diskHealth)
    {
        // Prefer the richer SMART/health disks; fall back to the basic snapshot disks.
        List<DiskReportInfo> disks = diskHealth.Count > 0
            ? diskHealth.Select(d => new DiskReportInfo(
                d.FriendlyName, d.MediaType, d.BusType, d.SizeGB, d.HealthStatus,
                string.IsNullOrWhiteSpace(d.Verdict) ? null : d.Verdict,
                d.TemperatureC, d.WearPercent,
                d.PowerOnHours.HasValue ? d.PowerOnDisplay : null)).ToList()
            : snapshot.Disks.Select(d => new DiskReportInfo(
                d.FriendlyName, d.MediaType, d.BusType, d.SizeGB, d.HealthStatus,
                null, d.TemperatureC, d.WearPercent, null)).ToList();

        return new SystemReportData(
            GeneratedAt: DateTime.Now,
            AppVersion: UpdateService.CurrentVersion.ToString(3),
            Os: snapshot.Os,
            Cpu: snapshot.Cpu,
            Memory: snapshot.Memory,
            Gpus: QueryGpus(),
            Motherboard: QueryMotherboard(),
            Disks: disks,
            NetworkAdapters: QueryNetworkAdapters());
    }

    private static List<GpuReportInfo> QueryGpus()
    {
        List<GpuReportInfo> gpus = [];
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, DriverVersion, PNPDeviceID FROM Win32_VideoController");
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    var name = mo["Name"]?.ToString()?.Trim() ?? "Unknown GPU";
                    // Win32_VideoController.AdapterRAM is a uint32 (CIM_UINT32) and saturates
                    // at ~4 GiB, so it mis-reports every modern >4 GB GPU. The true size lives
                    // in the driver's registry key as a 64-bit qwMemorySize; prefer it and fall
                    // back to AdapterRAM only when the registry value is missing/zero.
                    var driver = mo["DriverVersion"]?.ToString()?.Trim() ?? "";
                    var pnpId = mo["PNPDeviceID"]?.ToString();
                    ulong? adapterRam = mo["AdapterRAM"] is { } ram ? Convert.ToUInt64(ram) : null;
                    var vram = GpuVramHelper.ResolveVramGB(pnpId, adapterRam);
                    gpus.Add(new GpuReportInfo(name, vram, driver));
                }
            }
        }
        catch (ManagementException ex) { Log.Debug("GPU info unavailable for report: {Error}", ex.Message); }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Debug("GPU info WMI COM error: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("GPU info access denied: {Error}", ex.Message); }
        return gpus;
    }

    private static string QueryMotherboard()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Product FROM Win32_BaseBoard");
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    var manufacturer = mo["Manufacturer"]?.ToString()?.Trim() ?? "";
                    var product = mo["Product"]?.ToString()?.Trim() ?? "";
                    var combined = $"{manufacturer} {product}".Trim();
                    if (!string.IsNullOrWhiteSpace(combined)) return combined;
                }
            }
        }
        catch (ManagementException ex) { Log.Debug("Motherboard info unavailable for report: {Error}", ex.Message); }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Debug("Motherboard info WMI COM error: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Motherboard info access denied: {Error}", ex.Message); }
        return "";
    }

    private static List<NetworkAdapterInfo> QueryNetworkAdapters()
    {
        List<NetworkAdapterInfo> adapters = [];
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Description, IPAddress, MACAddress, DHCPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    var desc = mo["Description"]?.ToString()?.Trim() ?? "Unknown adapter";
                    var mac = mo["MACAddress"]?.ToString() ?? "";
                    var dhcp = mo["DHCPEnabled"] is true;
                    var ipv4 = "";
                    if (mo["IPAddress"] is string[] addresses)
                        ipv4 = addresses.FirstOrDefault(a => !a.Contains(':')) ?? "";
                    adapters.Add(new NetworkAdapterInfo(desc, ipv4, mac, dhcp));
                }
            }
        }
        catch (ManagementException ex) { Log.Debug("Network info unavailable for report: {Error}", ex.Message); }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Debug("Network info WMI COM error: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Network info access denied: {Error}", ex.Message); }
        return adapters;
    }

    // ── Plain-text rendering (preserves the original format) ──────────────────

    internal static string BuildText(SystemReportData d)
    {
        var sb = new StringBuilder(4096);

        sb.AppendLine("═══════════════════════════════════════════");
        sb.AppendLine("  SysManager System Report");
        sb.AppendLine($"  Generated: {d.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        sb.AppendLine("═══════════════════════════════════════════");
        sb.AppendLine();

        // First, because it is what the person reading the report needs before any of the inventory below.
        if (d.Health is { } health) AppendHealth(sb, health);

        AppendSection(sb, "Operating System");
        sb.AppendLine($"  {d.Os.Caption}");
        if (!string.IsNullOrWhiteSpace(d.Os.Version)) sb.AppendLine($"  Version: {d.Os.Version}");
        if (!string.IsNullOrWhiteSpace(d.Os.BuildNumber)) sb.AppendLine($"  Build: {d.Os.BuildNumber}");
        if (!string.IsNullOrWhiteSpace(d.Os.Architecture)) sb.AppendLine($"  Architecture: {d.Os.Architecture}");
        if (d.Os.Uptime > TimeSpan.Zero)
            sb.AppendLine($"  Uptime: {(int)d.Os.Uptime.TotalDays} days, {d.Os.Uptime.Hours} hours");
        sb.AppendLine();

        AppendSection(sb, "CPU");
        sb.Append($"  {d.Cpu.Name}");
        if (d.Cpu.Cores > 0) sb.Append($" ({d.Cpu.Cores} cores / {d.Cpu.LogicalProcessors} threads)");
        sb.AppendLine();
        if (d.Cpu.MaxClockMHz > 0) sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Base: {d.Cpu.MaxClockMHz / 1000.0:F1} GHz"));
        sb.AppendLine();

        AppendSection(sb, "Memory");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {d.Memory.TotalGB:F1} GB total"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Used: {d.Memory.UsedGB:F1} / {d.Memory.TotalGB:F1} GB ({d.Memory.UsedPercent:F0}%)"));
        if (d.Memory.Modules.Count > 0)
        {
            sb.AppendLine("  Slots:");
            foreach (var mod in d.Memory.Modules)
            {
                sb.Append($"    {mod.Slot}: {mod.CapacityGB:F0} GB");
                if (!string.IsNullOrWhiteSpace(mod.Manufacturer)) sb.Append($" {mod.Manufacturer}");
                if (mod.SpeedMHz > 0) sb.Append($" {mod.SpeedMHz} MHz");
                // Only worth printing when it disagrees with the rating — that is the actionable case.
                if (mod.IsUnderclocked) sb.Append($" (running at {mod.ConfiguredSpeedMHz} MHz)");
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        AppendSection(sb, "GPU");
        if (d.Gpus.Count > 0)
        {
            foreach (var g in d.Gpus)
            {
                sb.Append($"  {g.Name}");
                if (g.VramGB.HasValue) sb.Append(string.Create(CultureInfo.InvariantCulture, $" — VRAM: {g.VramGB:F1} GB"));
                if (!string.IsNullOrWhiteSpace(g.DriverVersion)) sb.Append($" — Driver: {g.DriverVersion}");
                sb.AppendLine();
            }
        }
        else sb.AppendLine("  (No GPU information available)");
        sb.AppendLine();

        AppendSection(sb, "Motherboard");
        sb.AppendLine(string.IsNullOrWhiteSpace(d.Motherboard) ? "  (No motherboard information available)" : $"  {d.Motherboard}");
        sb.AppendLine();

        AppendSection(sb, "Storage");
        if (d.Disks.Count > 0)
        {
            foreach (var disk in d.Disks)
            {
                sb.Append($"  {disk.FriendlyName}");
                if (!string.IsNullOrWhiteSpace(disk.MediaType) && disk.MediaType != "Unspecified") sb.Append($" — {disk.MediaType}");
                if (!string.IsNullOrWhiteSpace(disk.BusType) && disk.BusType != "Other") sb.Append($" ({disk.BusType})");
                if (disk.SizeGB > 0) sb.Append($" — {disk.SizeGB:F0} GB");
                sb.Append($" — {disk.HealthStatus}");
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(disk.Verdict)) sb.AppendLine($"    {disk.Verdict}");
                if (disk.TemperatureC.HasValue) sb.AppendLine($"    Temperature: {disk.TemperatureC:F0} °C");
                if (disk.WearPercent.HasValue) sb.AppendLine($"    Wear: {disk.WearPercent}%");
                if (!string.IsNullOrWhiteSpace(disk.PowerOnDisplay)) sb.AppendLine($"    Power-on: {disk.PowerOnDisplay}");
            }
        }
        else sb.AppendLine("  (No disk information available)");
        sb.AppendLine();

        AppendSection(sb, "Network");
        if (d.NetworkAdapters.Count > 0)
        {
            foreach (var n in d.NetworkAdapters)
            {
                sb.Append($"  {n.Description}");
                if (!string.IsNullOrWhiteSpace(n.IPv4)) sb.Append($" — {n.IPv4}");
                sb.Append($" — MAC: {n.MacAddress}");
                sb.Append($" — {(n.DhcpEnabled ? "DHCP" : "Static")}");
                sb.AppendLine();
            }
        }
        else sb.AppendLine("  (No active network adapters found)");
        sb.AppendLine();

        sb.AppendLine("───────────────────────────────────────────");
        sb.AppendLine($"  End of report. Generated by SysManager v{d.AppVersion}");

        return sb.ToString();
    }

    /// <summary>The health verdict and the recent problems, as the two sections the text report opens with.</summary>
    private static void AppendHealth(StringBuilder sb, ReportHealth health)
    {
        AppendSection(sb, "Health");
        sb.AppendLine($"  Overall: {health.Score}/100 ({health.Label})");
        foreach (var component in health.Components)
            sb.AppendLine($"  {component.Name}: {ComponentScore(component)}");
        if (health.Recommendations.Count == 0)
        {
            sb.AppendLine("  What to do: nothing needs attention.");
        }
        else
        {
            sb.AppendLine("  What to do:");
            for (var i = 0; i < health.Recommendations.Count; i++)
                sb.AppendLine($"    {i + 1}. {health.Recommendations[i].Message}");
        }
        sb.AppendLine();

        AppendSection(sb, ProblemsTitle);
        if (!health.ProblemsRead)
            sb.AppendLine("  (The System log could not be read.)");
        else if (health.RecentProblems.Count == 0)
            sb.AppendLine("  None.");
        foreach (var p in health.RecentProblems)
        {
            sb.AppendLine($"  {ProblemLine(p)}");
            if (!string.IsNullOrWhiteSpace(p.Explanation)) AppendWrapped(sb, "    ", p.Explanation);
            if (!string.IsNullOrWhiteSpace(p.Recommendation)) AppendWrapped(sb, "    ", $"What to do: {p.Recommendation}");
        }
        if (health.RecentProblems.Any(p => string.IsNullOrWhiteSpace(p.Explanation)))
            AppendWrapped(sb, "  ", UnexplainedNote);
        sb.AppendLine();
    }

    /// <summary>The widest a wrapped line of the text report gets.</summary>
    internal const int TextWidth = 100;

    /// <summary>
    /// Writes <paramref name="text"/> indented and wrapped between words at <see cref="TextWidth"/>. An event's
    /// explanation and advice run to 150 characters, and the tab's preview does not wrap, so unwrapped they
    /// could only be read by scrolling sideways.
    /// </summary>
    private static void AppendWrapped(StringBuilder sb, string indent, string text)
    {
        var line = new StringBuilder(indent);
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > indent.Length && line.Length + 1 + word.Length > TextWidth)
            {
                sb.AppendLine(line.ToString());
                line.Clear().Append(indent);
            }
            if (line.Length > indent.Length) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > indent.Length) sb.AppendLine(line.ToString());
    }

    /// <summary>The title both renderers give the problems list, with the window it covers.</summary>
    private static string ProblemsTitle => $"System log problems (last {(int)RecentProblemsWindow.TotalDays} days)";

    /// <summary>
    /// Said once under the list rather than on every line, so five unexplained events do not read as the same
    /// apology five times.
    /// </summary>
    private const string UnexplainedNote =
        "A line with no explanation is an error SysManager has no plain-English note for yet; its source and "
        + "event ID are what to search for.";

    /// <summary>A component's score out of 100, or that Windows gave nothing to score it from.</summary>
    private static string ComponentScore(ReportHealthComponent component) =>
        component.Score is { } score ? $"{score}/100" : "could not be read";

    /// <summary>When a problem was last logged, by what, and how often.</summary>
    private static string ProblemLine(ReportProblem p) =>
        $"{p.LastSeen.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} · {p.Source} {p.EventId}"
        + (p.Critical ? " · critical" : "")
        + (p.Count == 1 ? "" : $" · {p.Count} times");

    private static void AppendSection(StringBuilder sb, string title)
    {
        sb.Append("── ");
        sb.Append(title);
        sb.Append(' ');
        var remaining = Math.Max(0, 42 - title.Length - 4);
        sb.Append('─', remaining);
        sb.AppendLine();
    }

    // ── HTML rendering ────────────────────────────────────────────────────────

    internal static string BuildHtml(SystemReportData d)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<title>SysManager System Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(":root{color-scheme:dark}");
        sb.AppendLine("body{font-family:Segoe UI,system-ui,sans-serif;background:#0d1117;color:#e6edf3;margin:0;padding:32px;line-height:1.5}");
        sb.AppendLine(".wrap{max-width:880px;margin:0 auto}");
        sb.AppendLine("h1{font-size:24px;margin:0 0 4px}");
        sb.AppendLine(".sub{color:#8b949e;font-size:13px;margin-bottom:24px}");
        sb.AppendLine("section{background:#161b22;border:1px solid #30363d;border-radius:10px;padding:16px 20px;margin:0 0 16px}");
        sb.AppendLine("h2{font-size:15px;margin:0 0 12px;color:#58a6ff;text-transform:uppercase;letter-spacing:.04em}");
        sb.AppendLine("table{width:100%;border-collapse:collapse;font-size:14px}");
        sb.AppendLine("td{padding:4px 8px;vertical-align:top}");
        sb.AppendLine("td.k{color:#8b949e;width:200px;white-space:nowrap}");
        sb.AppendLine(".foot{color:#8b949e;font-size:12px;text-align:center;margin-top:24px}");
        sb.AppendLine("</style></head><body><div class=\"wrap\">");

        sb.AppendLine($"<h1>SysManager System Report</h1>");
        sb.AppendLine($"<div class=\"sub\">Generated {H(d.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))} · SysManager v{H(d.AppVersion)}</div>");

        // Health first, in the same section and table as everything else, for the reason the text report gives.
        if (d.Health is { } health)
        {
            OpenSection(sb, "Health");
            Row(sb, "Overall", $"{health.Score}/100 · {health.Label}");
            foreach (var component in health.Components)
                Row(sb, component.Name, ComponentScore(component));
            if (health.Recommendations.Count == 0)
                Row(sb, "What to do", "Nothing needs attention.");
            for (var i = 0; i < health.Recommendations.Count; i++)
                Row(sb, i == 0 ? "What to do" : "", $"{i + 1}. {health.Recommendations[i].Message}");
            CloseSection(sb);

            OpenSection(sb, ProblemsTitle);
            if (!health.ProblemsRead)
                Row(sb, "Problems", "The System log could not be read.");
            else if (health.RecentProblems.Count == 0)
                Row(sb, "Problems", "None.");
            foreach (var p in health.RecentProblems)
            {
                var v = $"{p.Source} {p.EventId}" + (p.Critical ? " · critical" : "")
                    + (p.Count == 1 ? "" : $" · {p.Count} times");
                if (!string.IsNullOrWhiteSpace(p.Explanation)) v += $" · {p.Explanation}";
                if (!string.IsNullOrWhiteSpace(p.Recommendation)) v += $" What to do: {p.Recommendation}";
                Row(sb, p.LastSeen.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), v);
            }
            if (health.RecentProblems.Any(p => string.IsNullOrWhiteSpace(p.Explanation)))
                Row(sb, "", UnexplainedNote);
            CloseSection(sb);
        }

        // OS
        OpenSection(sb, "Operating System");
        Row(sb, "Edition", d.Os.Caption);
        if (!string.IsNullOrWhiteSpace(d.Os.Version)) Row(sb, "Version", d.Os.Version);
        if (!string.IsNullOrWhiteSpace(d.Os.BuildNumber)) Row(sb, "Build", d.Os.BuildNumber);
        if (!string.IsNullOrWhiteSpace(d.Os.Architecture)) Row(sb, "Architecture", d.Os.Architecture);
        if (d.Os.Uptime > TimeSpan.Zero) Row(sb, "Uptime", $"{(int)d.Os.Uptime.TotalDays} days, {d.Os.Uptime.Hours} hours");
        CloseSection(sb);

        // CPU
        OpenSection(sb, "CPU");
        Row(sb, "Processor", d.Cpu.Name);
        if (d.Cpu.Cores > 0) Row(sb, "Cores / Threads", $"{d.Cpu.Cores} / {d.Cpu.LogicalProcessors}");
        if (d.Cpu.MaxClockMHz > 0) Row(sb, "Base clock", string.Create(CultureInfo.InvariantCulture, $"{d.Cpu.MaxClockMHz / 1000.0:F1} GHz"));
        CloseSection(sb);

        // Memory
        OpenSection(sb, "Memory");
        Row(sb, "Total", string.Create(CultureInfo.InvariantCulture, $"{d.Memory.TotalGB:F1} GB"));
        Row(sb, "In use", string.Create(CultureInfo.InvariantCulture, $"{d.Memory.UsedGB:F1} / {d.Memory.TotalGB:F1} GB ({d.Memory.UsedPercent:F0}%)"));
        foreach (var mod in d.Memory.Modules)
        {
            var v = $"{mod.CapacityGB:F0} GB";
            if (!string.IsNullOrWhiteSpace(mod.Manufacturer)) v += $" · {mod.Manufacturer}";
            if (mod.SpeedMHz > 0) v += $" · {mod.SpeedMHz} MHz";
            if (mod.IsUnderclocked) v += $" · running at {mod.ConfiguredSpeedMHz} MHz";
            Row(sb, mod.Slot, v);
        }
        CloseSection(sb);

        // GPU
        OpenSection(sb, "GPU");
        if (d.Gpus.Count > 0)
            foreach (var g in d.Gpus)
            {
                var v = g.Name;
                if (g.VramGB.HasValue) v += string.Create(CultureInfo.InvariantCulture, $" · {g.VramGB:F1} GB VRAM");
                if (!string.IsNullOrWhiteSpace(g.DriverVersion)) v += $" · driver {g.DriverVersion}";
                Row(sb, "Adapter", v);
            }
        else Row(sb, "Adapter", "(none detected)");
        CloseSection(sb);

        // Motherboard
        OpenSection(sb, "Motherboard");
        Row(sb, "Board", string.IsNullOrWhiteSpace(d.Motherboard) ? "(unknown)" : d.Motherboard);
        CloseSection(sb);

        // Storage
        OpenSection(sb, "Storage");
        if (d.Disks.Count > 0)
            foreach (var disk in d.Disks)
            {
                var v = disk.FriendlyName;
                if (!string.IsNullOrWhiteSpace(disk.MediaType) && disk.MediaType != "Unspecified") v += $" · {disk.MediaType}";
                if (!string.IsNullOrWhiteSpace(disk.BusType) && disk.BusType != "Other") v += $" · {disk.BusType}";
                if (disk.SizeGB > 0) v += $" · {disk.SizeGB:F0} GB";
                v += $" · {disk.HealthStatus}";
                if (disk.TemperatureC.HasValue) v += $" · {disk.TemperatureC:F0} °C";
                if (disk.WearPercent.HasValue) v += $" · wear {disk.WearPercent}%";
                if (!string.IsNullOrWhiteSpace(disk.PowerOnDisplay)) v += $" · power-on {disk.PowerOnDisplay}";
                Row(sb, "Drive", v);
            }
        else Row(sb, "Drive", "(none detected)");
        CloseSection(sb);

        // Network
        OpenSection(sb, "Network");
        if (d.NetworkAdapters.Count > 0)
            foreach (var n in d.NetworkAdapters)
            {
                var v = n.Description;
                if (!string.IsNullOrWhiteSpace(n.IPv4)) v += $" · {n.IPv4}";
                if (!string.IsNullOrWhiteSpace(n.MacAddress)) v += $" · MAC {n.MacAddress}";
                v += n.DhcpEnabled ? " · DHCP" : " · Static";
                Row(sb, "Adapter", v);
            }
        else Row(sb, "Adapter", "(no active adapters)");
        CloseSection(sb);

        // Reworded. It used to read "no data leaves this machine", which is true of the APPLICATION and
        // beside the point printed at the bottom of a file whose whole purpose is to be sent to somebody —
        // it reassured about the wrong thing (#2352). The footer now says what is true of the FILE.
        sb.AppendLine("<div class=\"foot\">Generated locally by SysManager. This report is safe to share: it "
                    + "lists your hardware and its health, not you — no user name, no computer name, no event "
                    + "messages, and network addresses are shortened so they cannot identify your machine.</div>");
        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    private static void OpenSection(StringBuilder sb, string title)
    {
        sb.Append("<section><h2>").Append(H(title)).AppendLine("</h2><table>");
    }

    private static void CloseSection(StringBuilder sb) => sb.AppendLine("</table></section>");

    private static void Row(StringBuilder sb, string key, string value)
        => sb.Append("<tr><td class=\"k\">").Append(H(key)).Append("</td><td>").Append(H(value)).AppendLine("</td></tr>");

    /// <summary>HTML-encodes a value so device names with &lt;, &gt;, &amp; can't break the markup.</summary>
    private static string H(string? s) => HtmlEncoder.Default.Encode(s ?? "");
}
