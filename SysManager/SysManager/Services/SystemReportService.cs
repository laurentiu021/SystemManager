// SysManager · SystemReportService — generates a comprehensive system report (text/HTML/JSON)
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Generates a full system report combining existing service data (OS, CPU, RAM,
/// Disks, SMART) with additional WMI queries (GPU, Motherboard, Network adapters).
/// The data is gathered once into a <see cref="SystemReportData"/> and rendered to
/// plain text, HTML, or JSON so all three formats share a single source of truth.
/// </summary>
public sealed class SystemReportService
{
    private readonly SystemInfoService _sysInfo;
    private readonly DiskHealthService _diskHealth;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // The report is written to a local file the user chooses; relax escaping so
        // names with '+'/'&' (e.g. "Notepad++") read naturally rather than as \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public SystemReportService(SystemInfoService sysInfo, DiskHealthService diskHealth)
    {
        _sysInfo = sysInfo;
        _diskHealth = diskHealth;
    }

    /// <summary>
    /// Gathers the full report payload (OS/CPU/RAM/GPU/motherboard/disks/network) once.
    /// </summary>
    public async Task<SystemReportData> GenerateDataAsync(CancellationToken ct = default)
    {
        var snapshot = await _sysInfo.CaptureAsync(ct).ConfigureAwait(false);
        var diskHealth = await _diskHealth.CollectAsync(ct).ConfigureAwait(false);

        return await Task.Run(() => BuildData(snapshot, diskHealth), ct).ConfigureAwait(false);
    }

    /// <summary>Generates a formatted plain-text system report.</summary>
    public async Task<string> GenerateReportAsync(CancellationToken ct = default)
        => BuildText(await GenerateDataAsync(ct).ConfigureAwait(false));

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
        List<DiskReportInfo> disks;
        if (diskHealth.Count > 0)
        {
            disks = diskHealth.Select(d => new DiskReportInfo(
                d.FriendlyName, d.MediaType, d.BusType, d.SizeGB, d.HealthStatus,
                string.IsNullOrWhiteSpace(d.Verdict) ? null : d.Verdict,
                d.TemperatureC, d.WearPercent,
                d.PowerOnHours.HasValue ? d.PowerOnDisplay : null)).ToList();
        }
        else
        {
            disks = snapshot.Disks.Select(d => new DiskReportInfo(
                d.FriendlyName, d.MediaType, d.BusType, d.SizeGB, d.HealthStatus,
                null, d.TemperatureC, d.WearPercent, null)).ToList();
        }

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
                "SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    var name = mo["Name"]?.ToString()?.Trim() ?? "Unknown GPU";
                    double? vram = null;
                    if (mo["AdapterRAM"] is { } ram)
                    {
                        var bytes = Convert.ToUInt64(ram);
                        if (bytes > 0) vram = bytes / 1024.0 / 1024.0 / 1024.0;
                    }
                    var driver = mo["DriverVersion"]?.ToString()?.Trim() ?? "";
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
        sb.AppendLine($"  Generated: {d.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("═══════════════════════════════════════════");
        sb.AppendLine();

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
        if (d.Cpu.MaxClockMHz > 0) sb.AppendLine($"  Base: {d.Cpu.MaxClockMHz / 1000.0:F1} GHz");
        sb.AppendLine();

        AppendSection(sb, "Memory");
        sb.AppendLine($"  {d.Memory.TotalGB:F1} GB total");
        sb.AppendLine($"  Used: {d.Memory.UsedGB:F1} / {d.Memory.TotalGB:F1} GB ({d.Memory.UsedPercent:F0}%)");
        if (d.Memory.Modules.Count > 0)
        {
            sb.AppendLine("  Slots:");
            foreach (var mod in d.Memory.Modules)
            {
                sb.Append($"    {mod.BankLabel}: {mod.CapacityGB:F0} GB");
                if (!string.IsNullOrWhiteSpace(mod.Manufacturer)) sb.Append($" {mod.Manufacturer}");
                if (mod.SpeedMHz > 0) sb.Append($" {mod.SpeedMHz} MHz");
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
                if (g.VramGB.HasValue) sb.Append($" — VRAM: {g.VramGB:F1} GB");
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
        sb.AppendLine($"<div class=\"sub\">Generated {H(d.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"))} · SysManager v{H(d.AppVersion)}</div>");

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
        if (d.Cpu.MaxClockMHz > 0) Row(sb, "Base clock", $"{d.Cpu.MaxClockMHz / 1000.0:F1} GHz");
        CloseSection(sb);

        // Memory
        OpenSection(sb, "Memory");
        Row(sb, "Total", $"{d.Memory.TotalGB:F1} GB");
        Row(sb, "In use", $"{d.Memory.UsedGB:F1} / {d.Memory.TotalGB:F1} GB ({d.Memory.UsedPercent:F0}%)");
        foreach (var mod in d.Memory.Modules)
        {
            var v = $"{mod.CapacityGB:F0} GB";
            if (!string.IsNullOrWhiteSpace(mod.Manufacturer)) v += $" · {mod.Manufacturer}";
            if (mod.SpeedMHz > 0) v += $" · {mod.SpeedMHz} MHz";
            Row(sb, mod.BankLabel, v);
        }
        CloseSection(sb);

        // GPU
        OpenSection(sb, "GPU");
        if (d.Gpus.Count > 0)
            foreach (var g in d.Gpus)
            {
                var v = g.Name;
                if (g.VramGB.HasValue) v += $" · {g.VramGB:F1} GB VRAM";
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

        sb.AppendLine("<div class=\"foot\">Generated locally by SysManager — no data leaves this machine.</div>");
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
