// SysManager · EventLogService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Buffers;
using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
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
    /// Queries a single log. Security requires admin; we silently skip on
    /// UnauthorizedAccessException so the rest of the dashboard still works.
    /// </summary>
    public IAsyncEnumerable<FriendlyEventEntry> ReadAsync(
        EventLogQueryOptions options, CancellationToken ct)
        => ReadInternal(options, ct);

    private async IAsyncEnumerable<FriendlyEventEntry> ReadInternal(
        EventLogQueryOptions opt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var xpath = BuildXPath(opt);
        EventLogReader? reader = null;
        try
        {
            var q = new EventLogQuery(opt.LogName, PathType.LogName, xpath)
            {
                ReverseDirection = true // newest first
            };
            reader = new EventLogReader(q);
        }
        catch (UnauthorizedAccessException) { yield break; }
        catch (EventLogNotFoundException) { yield break; }
        catch (EventLogException) { yield break; }

        int emitted = 0;
        using (reader)
        {
            var localReader = reader;
            while (!ct.IsCancellationRequested && emitted < opt.MaxResults)
            {
                // ReadEvent() is a blocking COM/IO call. Run it on a thread-pool
                // thread so enumerating large logs never blocks the UI thread the
                // caller awaits on. (await Task.Yield() alone did not move the work
                // off the UI thread — it only released it momentarily per 200 rows.)
                EventRecord? rec;
                try { rec = await Task.Run(() => localReader.ReadEvent(), ct).ConfigureAwait(false); }
                catch (EventLogException) { continue; }
                catch (OperationCanceledException) { yield break; }
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
            Xml = rec.ToXml(),
            MachineName = rec.MachineName,
            UserName = rec.UserId?.Value,
            RecordId = rec.RecordId ?? 0
        };
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
            var levels = opt.Severities.Select(s => (int)SeverityToLevel(s)).Distinct().ToList();
            clauses.Add("(" + string.Join(" or ", levels.Select(l => $"Level={l}")) + ")");
        }

        if (opt.Since.HasValue)
        {
            var iso = opt.Since.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
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

    private static byte SeverityToLevel(EventSeverity s) => s switch
    {
        EventSeverity.Critical => 1,
        EventSeverity.Error => 2,
        EventSeverity.Warning => 3,
        EventSeverity.Info => 4,
        EventSeverity.Verbose => 5,
        _ => 4
    };
}
