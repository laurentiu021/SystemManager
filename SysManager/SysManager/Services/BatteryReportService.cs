// SysManager · BatteryReportService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.IO;
using Serilog;

namespace SysManager.Services;

/// <summary>
/// Reads the capacity history Windows keeps for the battery, by asking <c>powercfg</c> for its battery report
/// as XML (#1513).
/// </summary>
/// <remarks>
/// <para><c>powercfg /batteryreport</c> needs no administrator rights: run here with the Administrators group
/// removed from the token, it wrote the same report. That matters because the tab's own capacity read goes
/// through <c>root\WMI</c>, which does need them.</para>
/// <para>The report is written to a new, randomly named file in the temp folder, read once and deleted, so
/// nothing of it stays behind and no earlier file can be mistaken for it. It goes through the shared
/// <see cref="IPowerShellRunner"/>, which resolves <c>powercfg.exe</c> to System32 rather than letting the
/// process search order pick a planted copy.</para>
/// </remarks>
public sealed class BatteryReportService : IBatteryReportService
{
    /// <summary>A report is tens of kilobytes; anything near this is not one, and is not read into memory.</summary>
    internal const long MaxReportBytes = 16 * 1024 * 1024;

    /// <summary>The report takes a second or two; a run past this has stalled.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IPowerShellRunner _runner;
    private readonly string _tempDir;
    private readonly TimeProvider _time;

    /// <summary>
    /// Writes the report under <paramref name="tempDir"/>, the user's temp folder when omitted, and times a run out
    /// on <paramref name="time"/>, the system clock when omitted. A test passes a clock whose timers fire only when it
    /// says so, so the timeout is provable without waiting thirty seconds.
    /// </summary>
    public BatteryReportService(IPowerShellRunner runner, string? tempDir = null, TimeProvider? time = null)
    {
        _runner = runner;
        _tempDir = tempDir ?? Path.GetTempPath();
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<BatteryReportRead> ReadHistoryAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(_tempDir, $"SysManager-battery-{Guid.NewGuid():N}.xml");
        using var timeout = new CancellationTokenSource(Timeout, _time);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            var exit = await _runner.RunProcessAsync(
                "powercfg.exe", $"/batteryreport /xml /output \"{path}\"", run.Token,
                PowerShellRunner.OemEncoding).ConfigureAwait(false);

            var file = new FileInfo(path);
            if (exit != 0 || !file.Exists || file.Length > MaxReportBytes)
            {
                Log.Warning("Battery report not produced: exit {Exit}, file {Exists}, {Bytes} bytes",
                    exit, file.Exists, file.Exists ? file.Length : 0);
                return BatteryReportRead.Failed;
            }

            var history = BatteryReportParser.ParseHistory(
                await File.ReadAllTextAsync(path, run.Token).ConfigureAwait(false));
            if (history is null) return BatteryReportRead.Failed;

            return new BatteryReportRead(
                history.Count == 0 ? BatteryReportOutcome.NoHistory : BatteryReportOutcome.Read, history);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning("Battery report timed out after {Seconds}s", Timeout.TotalSeconds);
            return BatteryReportRead.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
                                       or InvalidOperationException)
        {
            Log.Warning("Battery report could not be read: {Error}", ex.Message);
            return BatteryReportRead.Failed;
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException ex) { Log.Debug("Battery report temp file left behind: {Error}", ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Debug("Battery report temp file left behind: {Error}", ex.Message); }
        }
    }
}
