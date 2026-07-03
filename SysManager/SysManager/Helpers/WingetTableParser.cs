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

    // Column titles winget prints, by column, across its shipped UI languages. A 4-column
    // table is ambiguous by position alone (Name/Id/Version/Source for `list` vs
    // Name/Id/Version/Available for a source-less `upgrade`), so we identify each optional
    // column by matching its header word against these known localized titles — never by
    // its ordinal position. Lower-cased for case-insensitive comparison.
    private static readonly string[] IdTitles = ["id", "kennung", "identifiant", "identificador", "identificatore", "識別子", "标识符", "식별자"];
    private static readonly string[] VersionTitles = ["version", "versione", "versión", "versão", "バージョン", "版本", "버전"];
    private static readonly string[] AvailableTitles = ["available", "verfügbar", "disponible", "disponibile", "disponível", "利用可能", "可用", "사용 가능"];
    private static readonly string[] SourceTitles = ["source", "quelle", "origine", "fuente", "fonte", "ソース", "源", "소스"];
    // "Match" (winget search) is not consumed by any caller; we don't need to localize it.

    // Column titles are localized (de-DE: "Name Kennung Version Verfügbar Quelle"), but the
    // column ORDER is stable. We locate each column by matching its header token against the
    // known localized titles above, so both the layout (which optional columns exist) and the
    // positions are correct on any UI language — without the position-only ambiguity of a
    // 4-column table. Name is always the first column; Id is the second column (its title set
    // is only used to confirm, falling back to the second token).
    private static ColumnLayout DetectColumns(string header)
    {
        var tokens = HeaderTokens(header);
        // Fewer than 3 columns is not a winget package table (need at least Name/Id/Version).
        if (tokens.Count < 3)
            return new ColumnLayout { Name = 0, Id = -1, Version = -1, Available = -1, Source = -1 };

        int id = FindStart(tokens, IdTitles);
        int version = FindStart(tokens, VersionTitles);
        int available = FindStart(tokens, AvailableTitles);
        int source = FindStart(tokens, SourceTitles);

        // Positional fallback for the Available/Source columns on an unlisted locale, but ONLY
        // for the UNAMBIGUOUS 5-column shape (Name Id Version Available Source). Without this,
        // an upgrade table in a locale we don't have titles for leaves Available = -1, so every
        // row's Available comes back blank and WingetService drops it — App Updates shows zero
        // upgrades. A 4-column table is deliberately left alone: it's ambiguous by position
        // (list = Name/Id/Version/Source vs source-less upgrade = Name/Id/Version/Available),
        // and prior fixes proved position can't disambiguate it — so we don't guess there.
        if (tokens.Count >= 5)
        {
            if (available < 0) available = tokens[3].Start;
            if (source < 0) source = tokens[4].Start;
        }

        return new ColumnLayout
        {
            Name = tokens[0].Start,
            // Fall back to the 2nd/3rd token when a title isn't in our set (unknown locale):
            // the Name/Id/Version order is invariant, so position is a safe last resort here.
            Id = id >= 0 ? id : tokens[1].Start,
            Version = version >= 0 ? version : tokens[2].Start,
            Available = available,
            Source = source
        };
    }

    // Start offset of the first header token whose (lower-cased) word is in titles, else -1.
    private static int FindStart(List<(int Start, string Word)> tokens, string[] titles)
    {
        foreach (var (start, word) in tokens)
            if (titles.Contains(word.ToLowerInvariant()))
                return start;
        return -1;
    }

    // Header tokens as (start offset, word). A "word" is a run of non-whitespace; note winget
    // titles are single words in every shipped locale, so one token == one column.
    private static List<(int Start, string Word)> HeaderTokens(string header)
    {
        List<(int, string)> tokens = [];
        int i = 0;
        while (i < header.Length)
        {
            if (char.IsWhiteSpace(header[i])) { i++; continue; }
            int start = i;
            while (i < header.Length && !char.IsWhiteSpace(header[i])) i++;
            tokens.Add((start, header[start..i]));
        }
        return tokens;
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
