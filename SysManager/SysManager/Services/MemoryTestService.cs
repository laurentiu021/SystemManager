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
    /// <summary>What the last 30 days of the System log say about the RAM.</summary>
    /// <param name="WheaMemoryErrors">WHEA errors that the manifest gives to memory.</param>
    /// <param name="MemoryDiagnosticErrors">Windows Memory Diagnostic results that found errors.</param>
    /// <param name="LastError">When the newest of them was logged.</param>
    public sealed record MemoryErrorSummary(
        int WheaMemoryErrors,
        int MemoryDiagnosticErrors,
        DateTime? LastError);

    private const string Query =
        $"*[System[Provider[@Name='{MemoryEvents.WheaProvider}' or @Name='{MemoryEvents.DiagnosticProvider}']]]";

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
        return await Task.Run(() => Summarize(ReadNewestFirst(ct), DateTime.Now.AddDays(-30)), ct).ConfigureAwait(false);
    }

    /// <summary>The two providers' records, newest first, each disposed as the next one is read.</summary>
    private IEnumerable<(string? Provider, int Id, DateTime? Created)> ReadNewestFirst(CancellationToken ct)
    {
        using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
            new System.Diagnostics.Eventing.Reader.EventLogQuery(_logName,
                System.Diagnostics.Eventing.Reader.PathType.LogName, Query)
            { ReverseDirection = true });

        // Check cancellation BEFORE reading so a record read at the moment of cancellation isn't left to the GC; the
        // read result is always wrapped in using(rec) below.
        while (!ct.IsCancellationRequested && reader.ReadEvent() is { } rec)
        {
            using (rec)
                yield return (rec.ProviderName, rec.Id, rec.TimeCreated);
        }
    }

    /// <summary>
    /// Counts the memory errors among <paramref name="newestFirst"/> that were logged since <paramref name="since"/>,
    /// by <see cref="MemoryEvents"/>' IDs.
    /// </summary>
    /// <remarks>
    /// The WHEA IDs counted were 17 to 20, which are PCI Express and processor errors, and the one Windows Memory
    /// Diagnostic result counted was 1201, a test that found no errors, while one that found errors was not (#2715).
    /// </remarks>
    internal static MemoryErrorSummary Summarize(
        IEnumerable<(string? Provider, int Id, DateTime? Created)> newestFirst, DateTime since)
    {
        int whea = 0, diagnostic = 0;
        DateTime? lastError = null;
        foreach (var (provider, id, created) in newestFirst)
        {
            if (created < since) break;

            if (MemoryEvents.IsWheaMemoryError(provider, id)) whea++;
            else if (MemoryEvents.IsDiagnosticError(provider, id)) diagnostic++;
            else continue;

            // Only advance lastError for records that actually count as errors.
            if (created.HasValue && (lastError is null || created > lastError)) lastError = created;
        }

        return new MemoryErrorSummary(whea, diagnostic, lastError);
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
