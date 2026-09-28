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

    // "System" everywhere but a test, which names a log that does not exist. That is the one way to make the
    // real reader fail on demand, and the failure path is the one that was wrong (#2479).
    private readonly string _logName;

    public MemoryTestService() : this("System") { }

    internal MemoryTestService(string logName) => _logName = logName;

    /// <summary>
    /// Look at the System event log for memory-related hardware errors.
    /// Returns counts for the last 30 days.
    /// </summary>
    /// <exception cref="System.Diagnostics.Eventing.Reader.EventLogException">The log could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Windows refused to let the log be read.</exception>
    /// <remarks>
    /// A log that could not be read is thrown, never returned as a summary. Both callers catch these two
    /// exceptions to say the check could not run, and this method used to catch them first and return zero
    /// errors, so the Dashboard and System Health both reported "No memory errors" in green for a log nobody had
    /// read, and their could-not-check branches never ran (#2479).
    /// </remarks>
    public async Task<MemoryErrorSummary> CheckErrorLogsAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            int wheaCount = 0, diagCount = 0;
            DateTime? lastError = null;

            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
                new System.Diagnostics.Eventing.Reader.EventLogQuery(_logName,
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

}
