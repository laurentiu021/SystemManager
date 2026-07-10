// SysManager · MemoryTestService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.Management;
using SysManager.Models;

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
                        "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger' or @Name='Microsoft-Windows-MemoryDiagnostics-Results']]]")
                    { ReverseDirection = true });

                var cutoff = DateTime.Now.AddDays(-30);
                // Check cancellation BEFORE reading so a record read at the moment of
                // cancellation isn't left to the GC; the read result is always wrapped
                // in using(rec) below.
                while (!ct.IsCancellationRequested && reader.ReadEvent() is { } rec)
                {
                    using (rec)
                    {
                        if (rec.TimeCreated.HasValue && rec.TimeCreated.Value < cutoff) break;

                        var provider = rec.ProviderName ?? "";
                        bool counted = false;
                        if (provider.Contains("WHEA"))
                        {
                            // Memory-related WHEA events are ID 17 / 18 / 19 / 20 typically
                            if (rec.Id == 17 || rec.Id == 18 || rec.Id == 19 || rec.Id == 20)
                            {
                                wheaCount++;
                                counted = true;
                            }
                        }
                        else if (provider.Contains("MemoryDiagnostics"))
                        {
                            // 1201 = errors detected. 1101 = test passed (no errors), which
                            // must NOT count as a memory error (previously any ID counted,
                            // turning a clean test into a false warning).
                            if (rec.Id == 1201)
                            {
                                diagCount++;
                                counted = true;
                            }
                        }
                        // Only advance lastError for records that actually count as errors.
                        if (counted && rec.TimeCreated.HasValue && (lastError is null || rec.TimeCreated.Value > lastError))
                            lastError = rec.TimeCreated.Value;
                    }
                }
            }
            catch (System.Diagnostics.Eventing.Reader.EventLogException) { /* EventLog API can throw on restricted hosts */ }
            catch (UnauthorizedAccessException) { /* EventLog access denied */ }

            return new MemoryErrorSummary(wheaCount, diagCount, lastError);
        }, ct).ConfigureAwait(false);
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
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = SysManager.Helpers.SystemPaths.ResolveSystemTool("mdsched.exe"),
                UseShellExecute = true
            });
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Read installed memory modules — fast WMI query.</summary>
    public async Task<IReadOnlyList<MemoryModuleHealth>> GetModulesAsync()
    {
        return await Task.Run<IReadOnlyList<MemoryModuleHealth>>(() =>
        {
            List<MemoryModuleHealth> list = [];
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT BankLabel, DeviceLocator, Manufacturer, Capacity, Speed, ConfiguredClockSpeed, PartNumber FROM Win32_PhysicalMemory");
                using var moc = s.Get();
                foreach (ManagementObject mo in moc)
                {
                    using (mo)
                    {
                        try
                        {
                            double cap = FixedDriveService.ToDoubleSafe(mo["Capacity"]) / 1024d / 1024d / 1024d;
                            list.Add(new MemoryModuleHealth
                            {
                                Slot = mo["DeviceLocator"]?.ToString() ?? mo["BankLabel"]?.ToString() ?? "",
                                Manufacturer = (mo["Manufacturer"]?.ToString() ?? "").Trim(),
                                CapacityGB = Math.Round(cap, 0),
                                SpeedMHz = FixedDriveService.ToUInt32Safe(mo["Speed"]),
                                ConfiguredSpeedMHz = FixedDriveService.ToUInt32Safe(mo["ConfiguredClockSpeed"]),
                                PartNumber = (mo["PartNumber"]?.ToString() ?? "").Trim()
                            });
                        }
                        catch (InvalidCastException) { /* malformed WMI value — skip this module, continue scanning */ }
                        catch (FormatException) { /* malformed WMI value — skip this module */ }
                        catch (OverflowException) { /* WMI value out of range — skip this module */ }
                    }
                }
            }
            catch (ManagementException) { /* WMI class not available */ }
            catch (UnauthorizedAccessException) { /* WMI access denied */ }
            return list;
        }).ConfigureAwait(false);
    }
}
