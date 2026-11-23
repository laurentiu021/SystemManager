// SysManager · BatteryReportService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.IO;
using System.Text;
using Serilog;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// Reads the capacity history Windows keeps for the battery, by asking <c>powercfg</c> for its battery report
/// as XML (#1513).
/// </summary>
/// <remarks>
/// <para><c>powercfg /batteryreport</c> needs no administrator rights: run here with the Administrators group
/// removed from the token, it wrote the same report. That matters because the tab's own capacity read goes
/// through <c>root\WMI</c>, which does need them.</para>
/// <para>The report is written to a new, randomly named file in the profile's own temp folder
/// (<see cref="SystemPaths.UserTempRoot"/>), never the one TMP or TEMP name, so nothing of it stays behind and no
/// earlier file can be mistaken for it. It is read and removed through one handle checked to be open on that file
/// (<see cref="SafeFileWalk.ReadThenDeleteFile"/>), never by its name: powercfg runs with SysManager's rights, also
/// as administrator, and a folder in the user's own profile can be turned into a link while it runs. It goes
/// through the shared <see cref="IPowerShellRunner"/>, which resolves <c>powercfg.exe</c> to System32 rather than
/// letting the process search order pick a planted copy.</para>
/// </remarks>
public sealed class BatteryReportService : IBatteryReportService
{
    /// <summary>A report is tens of kilobytes; anything near this is not one, and is not read into memory.</summary>
    internal const long MaxReportBytes = 16 * 1024 * 1024;

    /// <summary>The report takes a second or two; a run past this has stalled.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IPowerShellRunner _runner;
    private readonly string? _tempDir;
    private readonly TimeProvider _time;

    /// <summary>
    /// Writes the report under <paramref name="tempDir"/>, the profile's own temp folder when omitted, and times a run
    /// out on <paramref name="time"/>, the system clock when omitted. A test passes a clock whose timers fire only
    /// when it says so, so the timeout is provable without waiting thirty seconds.
    /// </summary>
    public BatteryReportService(IPowerShellRunner runner, string? tempDir = null, TimeProvider? time = null)
    {
        _runner = runner;
        _tempDir = tempDir ?? SystemPaths.UserTempRoot;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<BatteryReportRead> ReadHistoryAsync(CancellationToken ct = default)
    {
        // A quote would end /output's argument early and hand the rest of the folder name to powercfg as switches,
        // and a folder reached through a link would have powercfg write wherever it leads.
        if (string.IsNullOrEmpty(_tempDir) || _tempDir.Contains('"') || SafeFileWalk.IsReachedThroughLink(_tempDir))
        {
            Log.Warning("Battery report not asked for: the temp folder is missing, is reached through a link, "
                + "or its name holds a quote");
            return BatteryReportRead.Failed;
        }

        var path = Path.Combine(_tempDir, $"SysManager-battery-{Guid.NewGuid():N}.xml");
        using var timeout = new CancellationTokenSource(Timeout, _time);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var taken = false;

        try
        {
            var exit = await _runner.RunProcessAsync(
                "powercfg.exe", $"/batteryreport /xml /output \"{path}\"", run.Token,
                PowerShellRunner.OemEncoding).ConfigureAwait(false);

            taken = true;
            var report = SafeFileWalk.ReadThenDeleteFile(path, MaxReportBytes);
            if (report is { Removed: false })
                Log.Debug("Battery report temp file left behind: Windows refused its removal");
            if (exit != 0 || report is not { Contents: { } contents })
            {
                Log.Warning("Battery report not produced: exit {Exit}, file {Exists}, {Bytes} bytes",
                    exit, report is not null, report?.Length ?? 0);
                return BatteryReportRead.Failed;
            }

            using var reader = new StreamReader(new MemoryStream(contents), Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            var text = await reader.ReadToEndAsync(run.Token).ConfigureAwait(false);
            var history = BatteryReportParser.ParseHistory(text);
            if (history is null) return BatteryReportRead.Failed;

            return new BatteryReportRead(
                history.Count == 0 ? BatteryReportOutcome.NoHistory : BatteryReportOutcome.Read, history);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning("Battery report timed out after {Seconds}s", Timeout.TotalSeconds);
            return BatteryReportRead.Failed;
        }
        catch (LeftInPlaceException)
        {
            Log.Warning("Battery report refused: its folder now leads somewhere else, so it was neither read nor "
                + "removed");
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
            // powercfg did not start, stalled or was cancelled: whatever it wrote goes the same checked way.
            if (!taken) LeaveNothingBehind(path);
        }
    }

    /// <summary>Removes what a run that did not finish may have written, through the checked handle.</summary>
    private static void LeaveNothingBehind(string path)
    {
        try
        {
            SafeFileWalk.DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Battery report temp file left behind: {Error}", ex.Message);
        }
    }
}
