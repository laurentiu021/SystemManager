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
    /// <c>MemoryTestService.CheckErrorLogsAsync</c>, where a reader fault ends the scan too.</para>
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
            try
            {
                // Severity is filtered HERE rather than in the query — see BuildXPath for why, because it
                // is the fix for a cancellation defect rather than a preference. Before Project on purpose:
                // Project formats the record's message, which is the expensive part, and a filtered read
                // discards most of what it reads.
                if (Matches(opt.Severities, rec.Level))
                    entry = Project(rec, opt.LogName);
            }
            catch (EventLogException) { /* skip malformed record */ }
            catch (InvalidOperationException) { /* skip malformed record */ }
            finally { rec.Dispose(); }

            // Null here means one of three things — filtered out, malformed, or unreadable — and all three
            // are "move on to the next record". Deliberately does NOT increment emitted, so MaxResults
            // still counts what the caller receives.
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

    /// <summary>
    /// One source's events in a window: how many there were of the asked severities, when the newest was logged, and
    /// whether any was critical.
    /// </summary>
    public sealed record EventGroup(string ProviderName, int EventId, int Count, DateTime LastSeen, bool Critical);

    /// <summary>The groups a tally found, and how the read went, for this read alone.</summary>
    public sealed record EventTally(IReadOnlyList<EventGroup> Groups, ReadOutcome Outcome);

    /// <summary>What a count needs of one record, which is no message: its source, ID, level and time.</summary>
    internal readonly record struct EventFacts(string ProviderName, int EventId, byte? Level, DateTime Time);

    /// <summary>How many records a tally reads per hop to the thread pool.</summary>
    internal const int TallyBatch = 256;

    /// <summary>
    /// Counts every record of the asked severities in the window, grouped by source and event ID, without reading a
    /// single message. <see cref="EventLogQueryOptions.MaxResults"/> does not apply: the count is of the whole window.
    /// </summary>
    /// <remarks>
    /// For System Report's problem list (#2827). It read the 500 newest errors and stopped, and one noisy source used
    /// those up in a day or two, so an older critical event was never read and a count stopped at 500. Formatting the
    /// message is the expensive part of a record, and a count needs none: on the PC it was measured on, counting a
    /// week of the System log took under half a second.
    /// <para>The outcome comes back with the groups rather than through <see cref="LastOutcome"/>, which two reports
    /// running at once on one instance would share: one read's reset could hide the other's fault.</para>
    /// </remarks>
    public async Task<EventTally> TallyAsync(EventLogQueryOptions opt, CancellationToken ct)
    {
        EventLogReader reader;
        try
        {
            reader = new EventLogReader(
                new EventLogQuery(opt.LogName, PathType.LogName, BuildXPath(opt)) { ReverseDirection = true });
        }
        catch (UnauthorizedAccessException) { return new([], ReadOutcome.AccessDenied); }
        catch (EventLogNotFoundException) { return new([], ReadOutcome.LogNotFound); }
        catch (EventLogException) { return new([], ReadOutcome.Unavailable); }

        using (reader)
            return await Tally(opt, () => NextFacts(reader, opt.LogName), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The count, over a caller-supplied record source that returns null at the end. Separate from
    /// <see cref="EventLogReader"/> for the reason <see cref="Enumerate"/> is: a real reader cannot be filled or made
    /// to fault on demand.
    /// </summary>
    /// <remarks>
    /// A fault ends the read as <see cref="ReadOutcome.Unavailable"/> with no groups, so the report says the log could
    /// not be read rather than listing part of it as all of it. A cancelled read throws.
    /// </remarks>
    internal static async Task<EventTally> Tally(
        EventLogQueryOptions opt, Func<EventFacts?> readNext, CancellationToken ct)
    {
        var groups = new Dictionary<(string ProviderName, int EventId), EventGroup>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            List<EventFacts> batch;
            try
            {
                batch = await Task.Run(() => ReadBatch(readNext), ct).ConfigureAwait(false);
            }
            catch (EventLogException ex)
            {
                Log.Debug(ex, "EventLog: read faulted on {Log}, ending the tally", opt.LogName);
                return new([], ReadOutcome.Unavailable);
            }

            foreach (var facts in batch)
            {
                if (!Matches(opt.Severities, facts.Level)) continue;
                var key = (facts.ProviderName, facts.EventId);
                var critical = MapLevel(facts.Level) == EventSeverity.Critical;
                groups[key] = groups.TryGetValue(key, out var group)
                    ? group with
                    {
                        Count = group.Count + 1,
                        LastSeen = facts.Time > group.LastSeen ? facts.Time : group.LastSeen,
                        Critical = group.Critical || critical,
                    }
                    : new EventGroup(facts.ProviderName, facts.EventId, 1, facts.Time, critical);
            }

            if (batch.Count < TallyBatch) return new([.. groups.Values], ReadOutcome.Ok);
        }
    }

    private static List<EventFacts> ReadBatch(Func<EventFacts?> readNext)
    {
        var batch = new List<EventFacts>(TallyBatch);
        while (batch.Count < TallyBatch && readNext() is { } facts)
            batch.Add(facts);
        return batch;
    }

    /// <summary>The next record's facts, past any that cannot be read, or null at the end.</summary>
    private static EventFacts? NextFacts(EventLogReader reader, string logName)
    {
        while (true)
        {
            using var rec = reader.ReadEvent();
            if (rec is null) return null;
            try
            {
                return new EventFacts(rec.ProviderName ?? "", rec.Id, rec.Level, rec.TimeCreated ?? DateTime.MinValue);
            }
            catch (EventLogException ex)
            {
                Log.Debug(ex, "EventLog: skipped a record on {Log} that could not be read", logName);
            }
            catch (InvalidOperationException ex)
            {
                Log.Debug(ex, "EventLog: skipped a record on {Log} that could not be read", logName);
            }
        }
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

    /// <summary>
    /// True when a record's raw <c>Level</c> satisfies the requested severities. An absent or empty filter
    /// accepts everything.
    /// </summary>
    /// <remarks>
    /// Uses the same <see cref="MapLevel"/> the projection uses, so a record cannot be admitted here and
    /// then labelled with a different severity in the list. Internal so the mapping is testable without a
    /// real <c>EventRecord</c>, which cannot be constructed.
    /// </remarks>
    internal static bool Matches(List<EventSeverity>? severities, byte? level) =>
        severities is not { Count: > 0 } || severities.Contains(MapLevel(level));

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

        // Severity is deliberately NOT a clause here, and putting it back would reintroduce a defect rather
        // than an optimisation. The Event Log service evaluates this query itself: a single ReadEvent() walks
        // records internally until one satisfies it, and returns only then. That call is a blocking native
        // call and no CancellationToken can interrupt it, so with a Level clause over a window that contains
        // few matches, one read blocked for 4 m 43 s on a CI runner against a 10-second token — Cancel did
        // nothing, the tab stayed busy, and Refresh stayed disabled because it is gated on not-busy.
        //
        // Without it, each ReadEvent() returns the next record in the window immediately and the loop's own
        // ct.IsCancellationRequested check between records becomes the cancellation point. See Matches(),
        // which applies the filter in managed code using the same MapLevel the projection uses.
        //
        // The trade is examining more records, not doing more total work: the OS was already walking them,
        // and it did so uninterruptibly. An unfiltered read — the Logs tab's default — is byte-for-byte
        // unchanged, because every record it reads is emitted either way.
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

}
