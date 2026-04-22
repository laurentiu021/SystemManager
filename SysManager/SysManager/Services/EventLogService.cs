// SysManager · EventLogService
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Diagnostics.Eventing.Reader;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads entries from the Windows event logs (System/Application/Security/Setup)
/// via the modern EventLogReader API and projects them into our friendly
/// FriendlyEventEntry model. Filtering is done with XPath to keep the OS-side
/// query fast and avoid pulling millions of rows into memory.
/// </summary>
public sealed class EventLogService
{
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
            while (!ct.IsCancellationRequested && emitted < opt.MaxResults)
            {
                EventRecord? rec = null;
                try { rec = reader.ReadEvent(); }
                catch (EventLogException) { continue; }
                if (rec == null) yield break;

                FriendlyEventEntry? entry = null;
                try { entry = Project(rec, opt.LogName); }
                catch { /* skip malformed record */ }
                finally { rec.Dispose(); }

                if (entry == null) continue;
                EventExplainer.Enrich(entry);

                emitted++;
                yield return entry;

                // Yield occasionally to keep the UI responsive on huge logs.
                if (emitted % 200 == 0) await Task.Yield();
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
        catch { /* ignore; fall back */ }

        // Fallback: assemble from properties so we at least show something.
        try
        {
            var parts = rec.Properties?.Select(p => p?.Value?.ToString() ?? "") ?? [];
            return string.Join(" ", parts).Trim();
        }
        catch { return "(message not available)"; }
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var i = s.IndexOfAny(new[] { '\r', '\n' });
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
        var clauses = new List<string>();

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
            var safe = opt.ProviderName.Replace("'", "");
            clauses.Add($"Provider[@Name='{safe}']");
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

/// <summary>Filter parameters for one query against one log.</summary>
public sealed class EventLogQueryOptions
{
    public string LogName { get; set; } = "System";
    public List<EventSeverity>? Severities { get; set; }
    public DateTime? Since { get; set; }
    public string? ProviderName { get; set; }
    public int? EventId { get; set; }
    public int MaxResults { get; set; } = 500;
}
