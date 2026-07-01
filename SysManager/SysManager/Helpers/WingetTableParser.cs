// SysManager · WingetTableParser — shared column-based table parser for winget CLI output
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.RegularExpressions;

namespace SysManager.Helpers;

/// <summary>
/// Parses fixed-width table output from winget.exe (list and upgrade commands).
/// Both commands produce the same column-based format with a header line followed
/// by a dashes separator and data rows.
/// </summary>
internal static partial class WingetTableParser
{
    /// <summary>
    /// Parsed column positions from a winget table header.
    /// </summary>
    internal readonly struct ColumnLayout
    {
        public int Name { get; init; }
        public int Id { get; init; }
        public int Version { get; init; }
        public int Available { get; init; }
        public int Source { get; init; }
    }

    /// <summary>
    /// A raw row parsed from the table. Callers map this to their specific model.
    /// </summary>
    internal readonly struct RawRow
    {
        public string Name { get; init; }
        public string Id { get; init; }
        public string Version { get; init; }
        public string Available { get; init; }
        public string Source { get; init; }
    }

    // A winget table always prints a separator row of dashes directly under the header,
    // in EVERY UI language (the dashes are not localized). Anchoring on it lets us find the
    // header — and derive columns positionally — without depending on the English words.
    [GeneratedRegex(@"^\s*-{3,}")]
    private static partial Regex DashesSeparator();

    /// <summary>
    /// Parses a winget table output into raw rows. The headerPattern identifies the
    /// header line in the common (English) case; when it doesn't match — e.g. a localized
    /// winget where the column titles are translated (de-DE "Name Kennung Version Verfügbar
    /// Quelle") — the header is located as the line directly above the dashes separator row,
    /// so the parser works on any UI language. summaryPattern identifies the termination line.
    /// </summary>
    internal static List<RawRow> Parse(
        List<string> lines,
        Regex headerPattern,
        Regex summaryPattern)
    {
        List<RawRow> rows = [];

        int headerIdx = LocateHeader(lines, headerPattern);
        if (headerIdx < 0) return rows;

        var header = lines[headerIdx];
        var layout = DetectColumns(header);
        if (layout.Id < 0 || layout.Version < 0) return rows;

        for (int i = headerIdx + 2; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("--")) continue;
            if (summaryPattern.IsMatch(line)) break;
            if (line.Length < layout.Version) continue;

            var name = Slice(line, layout.Name, layout.Id);
            var id = Slice(line, layout.Id, layout.Version);
            var version = Slice(line, layout.Version, layout.Available >= 0 ? layout.Available : (layout.Source >= 0 ? layout.Source : -1));
            var available = layout.Available >= 0 ? Slice(line, layout.Available, layout.Source >= 0 ? layout.Source : -1) : "";
            var source = layout.Source >= 0 ? Slice(line, layout.Source, -1) : "";

            if (string.IsNullOrWhiteSpace(id)) continue;

            rows.Add(new RawRow
            {
                Name = name,
                Id = id,
                Version = version,
                Available = available,
                Source = source
            });
        }

        return rows;
    }

    /// <summary>
    /// Returns the index of the header row. Prefers the caller's (English) headerPattern;
    /// if that never matches — a localized winget — falls back to the line immediately above
    /// the first dashes separator, which winget prints in every language.
    /// </summary>
    private static int LocateHeader(List<string> lines, Regex headerPattern)
    {
        int byPattern = lines.FindIndex(l => headerPattern.IsMatch(l));
        if (byPattern >= 0) return byPattern;

        int sepIdx = lines.FindIndex(l => DashesSeparator().IsMatch(l));
        // The header is the row right above the separator, and it must carry column titles
        // (non-blank, not itself a dashes row).
        if (sepIdx > 0 && !string.IsNullOrWhiteSpace(lines[sepIdx - 1]))
            return sepIdx - 1;

        return -1;
    }

    // Column titles are localized (de-DE: "Name Kennung Version Verfügbar Quelle"), but the
    // column ORDER is stable across every winget UI language: Name, Id, Version, then the
    // optional Available (upgrade) / Match (search) and Source. So we map columns POSITIONALLY
    // by the start offset of each whitespace-delimited header token, never by the English word.
    private static ColumnLayout DetectColumns(string header)
    {
        var starts = TokenStarts(header);
        // Fewer than 3 columns is not a winget package table (need at least Name/Id/Version).
        if (starts.Count < 3)
            return new ColumnLayout { Name = 0, Id = -1, Version = -1, Available = -1, Source = -1 };

        int Col(int i) => i < starts.Count ? starts[i] : -1;
        return new ColumnLayout
        {
            Name = starts[0],
            Id = Col(1),
            Version = Col(2),
            Available = Col(3),
            Source = Col(4)
        };
    }

    // Start offsets of each run of non-whitespace in the header line (one per column title).
    private static List<int> TokenStarts(string header)
    {
        List<int> starts = [];
        bool inToken = false;
        for (int i = 0; i < header.Length; i++)
        {
            bool ws = char.IsWhiteSpace(header[i]);
            if (!ws && !inToken) { starts.Add(i); inToken = true; }
            else if (ws) inToken = false;
        }
        return starts;
    }

    private static string Slice(string line, int start, int end)
    {
        if (start < 0 || start >= line.Length) return string.Empty;
        int actualEnd = end < 0 ? line.Length : Math.Min(end, line.Length);
        // Guard against out-of-order columns (start > end): winget can emit headers
        // whose detected positions don't increase monotonically, which would make the
        // range operator throw ArgumentOutOfRangeException.
        if (actualEnd <= start) return string.Empty;
        return line[start..actualEnd].Trim();
    }
}
