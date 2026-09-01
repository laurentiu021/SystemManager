// SysManager · EventLogService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Buffers;
using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads entries from the Windows event logs (System/Application/Security/Setup)
/// via the modern EventLogReader API and projects them into our friendly
/// FriendlyEventEntry model. Filtering is done with XPath to keep the OS-side
/// query fast and avoid pulling millions of rows into memory.
/// </summary>
public sealed partial class EventLogService
{
    // Conservative allowlist for Windows event-log provider names:
    // letters, digits, space, dot, dash, underscore. Anything else is rejected.
    [GeneratedRegex(@"\A[A-Za-z0-9 ._-]{1,255}\z")]
    private static partial Regex ProviderNameRegex();

    /// <summary>
    /// Why a query produced no entries. An empty result and a refused result look identical
    /// to the caller otherwise, which made the Security log (readable only when elevated)
    /// report "No events match your filters" to a standard user.
    /// </summary>
    public enum ReadOutcome
    {
        /// <summary>The log was read; any emptiness is genuine.</summary>
        Ok,

        /// <summary>Windows refused access — the Security log needs elevation.</summary>
        AccessDenied,

        /// <summary>The named log does not exist on this machine.</summary>
        LogNotFound,

        /// <summary>The log exists but could not be opened for another reason.</summary>
        Unavailable
    }

    /// <summary>
    /// Set by the last <see cref="ReadAsync"/> enumeration that reached the open step, so the
    /// caller can tell "nothing matched" from "not allowed to look". Written before any entry
    /// is yielded and readable once enumeration completes.
    /// </summary>
    public ReadOutcome LastOutcome { get; private set; } = ReadOutcome.Ok;

    /// <summary>
    /// Queries a single log. The Security log requires elevation; when access is refused the
    /// enumeration ends without entries and <see cref="LastOutcome"/> reports why, so the rest
    /// of the dashboard still works and the UI can explain the gap instead of implying the log
    /// is empty.
    /// </summary>
    public IAsyncEnumerable<FriendlyEventEntry> ReadAsync(
        EventLogQueryOptions options, CancellationToken ct)
        => ReadInternal(options, ct);

    private async IAsyncEnumerable<FriendlyEventEntry> ReadInternal(
        EventLogQueryOptions opt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var xpath = BuildXPath(opt);
        EventLogReader? reader = null;
        LastOutcome = ReadOutcome.Ok;
        try
        {
            var q = new EventLogQuery(opt.LogName, PathType.LogName, xpath)
            {
                ReverseDirection = true // newest first
            };
            reader = new EventLogReader(q);
        }
        catch (UnauthorizedAccessException) { LastOutcome = ReadOutcome.AccessDenied; yield break; }
        catch (EventLogNotFoundException) { LastOutcome = ReadOutcome.LogNotFound; yield break; }
        catch (EventLogException) { LastOutcome = ReadOutcome.Unavailable; yield break; }

        using (reader)
        {
            var localReader = reader;
            await foreach (var entry in Enumerate(opt, () => localReader.ReadEvent(), ct).ConfigureAwait(false))
                yield return entry;
        }
    }

    /// <summary>
    /// The read loop, over a caller-supplied record source.
    /// </summary>
    /// <param name="opt">Query options; only <see cref="EventLogQueryOptions.MaxResults"/> and the log name are used here.</param>
    /// <param name="readNext">
    /// Produces the next record, or null at the end of the result set. Separated from
    /// <see cref="EventLogReader"/> so the failure and cancellation paths can be tested: there is no way to
    /// make a real reader fault on demand, and those two paths held the two worst defects in this file.
    /// </param>
    /// <param name="ct">Cancellation token; observed between records and inside the thread-pool hop.</param>
    /// <remarks>
    /// A reader fault ENDS the enumeration rather than retrying it. The previous code did
    /// <c>catch (EventLogException) { continue; }</c>, and since the loop's only progress variable is the
    /// emitted count — which a failure never increments — a persistent fault spun with no delay, queueing one
    /// <see cref="Task.Run(Action)"/> per iteration: the tab never finished loading, Refresh stayed disabled
    /// because it is gated on not-busy, and a core sat at 100%.
    /// <para>Terminating is not merely the simpler choice. <c>EvtNext</c> does not advance its cursor on a
    /// stale result set or a lost Event Log service, so a retry re-throws the identical error; a retry budget
    /// would postpone the same exit and add a number nobody can tune. It also matches
    /// <c>MemoryTestService.CheckErrorLogsAsync</c>, whose catch already terminates its scan.</para>
    /// </remarks>
    internal async IAsyncEnumerable<FriendlyEventEntry> Enumerate(
        EventLogQueryOptions opt,
        Func<EventRecord?> readNext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var emitted = 0;
        while (!ct.IsCancellationRequested && emitted < opt.MaxResults)
        {
            // readNext() is a blocking COM/IO call. Run it on a thread-pool thread so enumerating large
            // logs never blocks the UI thread the caller awaits on. (await Task.Yield() alone did not move
            // the work off the UI thread — it only released it momentarily per 200 rows.)
            //
            // Cancellation is deliberately NOT caught: the TaskCanceledException from Task.Run must reach
            // the caller, or Cancel reads as a completed load.
            EventRecord? rec = null;
            var faulted = false;
            try { rec = await Task.Run(readNext, ct).ConfigureAwait(false); }
            catch (EventLogException ex)
            {
                // Debug rather than Warning: a log rolling over mid-enumeration is routine, and the outcome
                // below is what the user is actually told.
                Log.Debug(ex, "EventLog: read faulted on {Log}, ending the enumeration", opt.LogName);
                faulted = true;
            }

            if (faulted)
            {
                LastOutcome = ReadOutcome.Unavailable;
                yield break;
            }

            if (rec is null) yield break;

            FriendlyEventEntry? entry = null;
            try { entry = Project(rec, opt.LogName); }
            catch (EventLogException) { /* skip malformed record */ }
            catch (InvalidOperationException) { /* skip malformed record */ }
            finally { rec.Dispose(); }

            if (entry is null) continue;
            EventExplainer.Enrich(entry);

            emitted++;
            yield return entry;
        }

        // A cancelled read exits the loop above with partial results; reporting them as a finished load
        // would mislead the user. Throw so the caller's cancel branch handles it — the same finalize
        // LargeFileScanner and DuplicateFileService use, and the reason LogsViewModel's
        // catch (OperationCanceledException) branch exists at all.
        //
        // Here rather than in ReadInternal: this is the loop whose condition exits on cancellation, so this
        // is where a cancelled read otherwise looks like a completed one. The throw propagates outward
        // through ReadInternal's await foreach.
        ct.ThrowIfCancellationRequested();
    }

    private static FriendlyEventEntry Project(EventRecord rec, string logName)
    {
        var severity = MapLevel(rec.Level);
        var fullMessage = SafeFormatMessage(rec);
        var firstLine = FirstLine(fullMessage);
        return new FriendlyEventEntry
        {
            Timestamp = rec.TimeCreated ?? DateTime.MinValue,
            LogName = logName,
            ProviderName = rec.ProviderName ?? "",
            EventId = rec.Id,
            Severity = severity,
            SeverityLabel = severity.ToString(),
            Message = firstLine,
            FullMessage = fullMessage,
            // Xml is deliberately NOT rendered here. Its only consumer is the detail pane's
            // SelectedEntry.Xml binding — one row at a time — while the largest MaxResults option is 5000,
            // so projecting it eagerly cost up to 5000 extra EvtRender round-trips per refresh and retained
            // 5000 strings for the lifetime of the tab. LogsViewModel fills it on selection via
            // GetXmlAsync. Unlike FullMessage, nothing filters on it.
            MachineName = rec.MachineName,
            UserName = rec.UserId?.Value,
            RecordId = rec.RecordId ?? 0
        };
    }

    /// <summary>
    /// Renders the raw XML of a single event, for the detail pane.
    /// </summary>
    /// <remarks>
    /// Looked up by record id rather than carried on every entry. See the note in <see cref="Project"/>:
    /// rendering XML for a whole result set paid thousands of COM round-trips and held thousands of strings
    /// to fill a field only the selected row shows. Returns an empty string when the record can no longer be
    /// found — an event log is a ring buffer, so a row visible in the list can genuinely have rolled off by
    /// the time it is clicked, and that is not an error worth a dialog.
    /// </remarks>
    public async Task<string> GetXmlAsync(string logName, long recordId, CancellationToken ct = default)
    {
        if (recordId <= 0) return "";

        return await Task.Run(() =>
        {
            try
            {
                var query = new EventLogQuery(logName, PathType.LogName,
                    $"*[System[EventRecordID={recordId}]]");
                using var reader = new EventLogReader(query);
                using var rec = reader.ReadEvent();
                return rec?.ToXml() ?? "";
            }
            catch (EventLogException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }, ct).ConfigureAwait(false);
    }

    private static string SafeFormatMessage(EventRecord rec)
    {
        try
        {
            var msg = rec.FormatDescription();
            if (!string.IsNullOrWhiteSpace(msg)) return msg;
        }
        catch (EventLogException) { /* format failed — fall back */ }
        catch (InvalidOperationException) { /* format failed — fall back */ }

        // Fallback: assemble from properties so we at least show something.
        try
        {
            var parts = rec.Properties?.Select(p => p?.Value?.ToString() ?? "") ?? [];
            return string.Join(" ", parts).Trim();
        }
        catch (EventLogException) { return "(message not available)"; }
        catch (InvalidOperationException) { return "(message not available)"; }
    }

    // Hoisted so the newline scan in FirstLine — run once per projected event
    // record, potentially thousands per query — doesn't allocate a char[] per call.
    private static readonly SearchValues<char> Newlines = SearchValues.Create("\r\n");

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var i = s.AsSpan().IndexOfAny(Newlines);
        return (i < 0 ? s : s[..i]).Trim();
    }

    private static EventSeverity MapLevel(byte? level) => level switch
    {
        1 => EventSeverity.Critical,
        2 => EventSeverity.Error,
        3 => EventSeverity.Warning,
        4 => EventSeverity.Info,
        5 => EventSeverity.Verbose,
        _ => EventSeverity.Info
    };

    /// <summary>
    /// Builds an XPath query string for EventLogQuery. Severity filter maps
    /// to Level numbers understood by the Event Log service.
    /// </summary>
    private static string BuildXPath(EventLogQueryOptions opt)
    {
        List<string> clauses = [];

        if (opt.Severities is { Count: > 0 })
        {
            var levels = opt.Severities.SelectMany(SeverityToLevels).Distinct().ToList();
            clauses.Add("(" + string.Join(" or ", levels.Select(l => $"Level={l}")) + ")");
        }

        if (opt.Since.HasValue)
        {
            // InvariantCulture is REQUIRED: the ':' in the format string is replaced by the
            // current culture's TimeSeparator, which is '.' on locales like fi-FI, producing
            // "12.30.45" — an invalid SystemTime that makes EventLogQuery throw (caught → the
            // Logs tab comes back empty). The Event Log XPath schema requires ISO-8601 with
            // ':' regardless of the OS display language.
            var iso = opt.Since.Value.ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            clauses.Add($"TimeCreated[@SystemTime>='{iso}']");
        }

        if (!string.IsNullOrWhiteSpace(opt.ProviderName))
        {
            // SEC-003: Allowlist rather than strip. A real Windows provider name is
            // letters/digits/dot/dash/underscore plus spaces. If it matches, use it
            // verbatim; if not, skip the provider clause entirely instead of silently
            // deleting metacharacters — which could mangle a legitimate name into a
            // different, wrong filter that quietly returns zero rows.
            if (ProviderNameRegex().IsMatch(opt.ProviderName))
                clauses.Add($"Provider[@Name='{opt.ProviderName}']");
        }

        if (opt.EventId.HasValue)
            clauses.Add($"EventID={opt.EventId.Value}");

        if (clauses.Count == 0) return "*";
        return "*[System[" + string.Join(" and ", clauses) + "]]";
    }

    /// <summary>
    /// Maps a severity back to ALL event levels that <see cref="MapLevel"/> folds into it.
    /// Used by <see cref="BuildXPath"/> so the XPath Level clause is the exact inverse of
    /// the read-side classification. In particular, Level 0 (LogAlways) is classified as
    /// Info by MapLevel, so Info must query BOTH Level=0 and Level=4.
    /// </summary>
    internal static IEnumerable<int> SeverityToLevels(EventSeverity s) => s switch
    {
        EventSeverity.Critical => [1],
        EventSeverity.Error => [2],
        EventSeverity.Warning => [3],
        EventSeverity.Info => [0, 4],
        EventSeverity.Verbose => [5],
        _ => [0, 4]
    };
}
