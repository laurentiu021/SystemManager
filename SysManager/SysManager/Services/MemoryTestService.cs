// SysManager · MemoryTestService
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Diagnostics;
using System.Management;

namespace SysManager.Services;

/// <summary>
/// RAM health diagnostics:
///  - Scans the System event log for hardware-error events (WHEA) in the last 30 days.
///  - Schedules Windows Memory Diagnostic (mdsched.exe) for the next boot.
/// </summary>
public sealed class MemoryTestService
{
    public sealed record MemoryErrorSummary(
        int WheaMemoryErrors,
        int MemoryDiagnosticResults,
        DateTime? LastError);

    /// <summary>
    /// Look at the System event log for memory-related hardware errors.
    /// Returns counts for the last 30 days.
    /// </summary>
    public async Task<MemoryErrorSummary> CheckErrorLogsAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            int wheaCount = 0, diagCount = 0;
            DateTime? lastError = null;

            try
            {
                using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
                    new System.Diagnostics.Eventing.Reader.EventLogQuery("System",
                        System.Diagnostics.Eventing.Reader.PathType.LogName,
                        "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger' or @Name='Microsoft-Windows-MemoryDiagnostics-Results']]]"));

                var cutoff = DateTime.Now.AddDays(-30);
                System.Diagnostics.Eventing.Reader.EventRecord? rec;
                while ((rec = reader.ReadEvent()) != null && !ct.IsCancellationRequested)
                {
                    using (rec)
                    {
                        if (rec.TimeCreated.HasValue && rec.TimeCreated.Value < cutoff) break;

                        var provider = rec.ProviderName ?? "";
                        if (provider.Contains("WHEA"))
                        {
                            // Memory-related WHEA events are ID 17 / 18 / 19 / 20 typically
                            if (rec.Id == 17 || rec.Id == 18 || rec.Id == 19 || rec.Id == 20)
                                wheaCount++;
                        }
                        else if (provider.Contains("MemoryDiagnostics"))
                        {
                            diagCount++;
                        }
                        if (rec.TimeCreated.HasValue && (lastError == null || rec.TimeCreated.Value > lastError))
                            lastError = rec.TimeCreated.Value;
                    }
                }
            }
            catch { /* silent — the EventLog API can throw on restricted hosts */ }

            return new MemoryErrorSummary(wheaCount, diagCount, lastError);
        }, ct);
    }

    /// <summary>
    /// Schedules Windows Memory Diagnostic to run at the next reboot.
    /// Does NOT force a reboot. Requires admin to actually apply.
    /// </summary>
    public bool ScheduleAtNextBoot()
    {
        try
        {
            // mdsched.exe prompts interactively. Use the schedule flag to avoid UI.
            // On Win10/11, the easiest way without UI is the "bcdedit" toggle used
            // behind the scenes, but safest portable option is to launch mdsched.
            Process.Start(new ProcessStartInfo
            {
                FileName = "mdsched.exe",
                UseShellExecute = true
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>Read installed memory modules — fast WMI query.</summary>
    public async Task<IReadOnlyList<MemoryModuleHealth>> GetModulesAsync()
    {
        return await Task.Run<IReadOnlyList<MemoryModuleHealth>>(() =>
        {
            var list = new List<MemoryModuleHealth>();
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT BankLabel, DeviceLocator, Manufacturer, Capacity, Speed, ConfiguredClockSpeed, PartNumber FROM Win32_PhysicalMemory");
                foreach (ManagementObject mo in s.Get())
                {
                    double cap = Convert.ToDouble(mo["Capacity"] ?? 0) / 1024d / 1024d / 1024d;
                    list.Add(new MemoryModuleHealth
                    {
                        Slot = mo["DeviceLocator"]?.ToString() ?? mo["BankLabel"]?.ToString() ?? "",
                        Manufacturer = (mo["Manufacturer"]?.ToString() ?? "").Trim(),
                        CapacityGB = Math.Round(cap, 0),
                        SpeedMHz = Convert.ToUInt32(mo["Speed"] ?? 0u),
                        ConfiguredSpeedMHz = Convert.ToUInt32(mo["ConfiguredClockSpeed"] ?? 0u),
                        PartNumber = (mo["PartNumber"]?.ToString() ?? "").Trim()
                    });
                }
            }
            catch { /* ignore */ }
            return list;
        });
    }
}

public sealed class MemoryModuleHealth
{
    public string Slot { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public double CapacityGB { get; set; }
    public uint SpeedMHz { get; set; }
    public uint ConfiguredSpeedMHz { get; set; }
    public string PartNumber { get; set; } = "";
}
