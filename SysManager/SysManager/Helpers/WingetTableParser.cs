// SysManager · WingetTableParser — shared column-based table parser for winget CLI output
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Text;
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
    /// Parsed column positions from a winget table header, in display columns as winget counts them.
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
    /// winget where the column titles are translated (de-DE "Name ID Version Verfügbar
    /// Quelle") — the header is located as the line directly above the dashes separator row,
    /// so the parser works on any UI language. summaryPattern identifies the termination line, and a second
    /// table below the first ends it too, without the heading and header lines above that table.
    /// </summary>
    internal static List<RawRow> Parse(
        List<string> lines,
        Regex headerPattern,
        Regex summaryPattern)
    {
        // Each row with the line it was read from, so the lines of a table that follows can be taken back out.
        List<(int Line, RawRow Row)> read = [];

        int headerIdx = LocateHeader(lines, headerPattern);
        if (headerIdx < 0) return [];

        var header = lines[headerIdx];
        var layout = DetectColumns(header);
        if (layout.Id < 0 || layout.Version < 0) return [];

        for (int i = headerIdx + 2; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Another table: winget prints one below the upgrade list for the packages that need to be named one by
            // one, and one for pinned packages, each under a heading line and its own header. Both were read as rows
            // of this table when its summary line was not recognised, so they go, and this table ends here (#2849).
            if (read.Count > 0 && DashesSeparator().IsMatch(line))
            {
                var heading = SecondLineAbove(lines, i);
                read.RemoveAll(r => r.Line >= heading);
                break;
            }

            if (line.StartsWith("--")) continue;
            if (summaryPattern.IsMatch(line)) break;
            if (CellsOf(line) < layout.Version) continue;

            var name = Slice(line, layout.Name, layout.Id);
            var id = Slice(line, layout.Id, layout.Version);
            var version = Slice(line, layout.Version, layout.Available >= 0 ? layout.Available : (layout.Source >= 0 ? layout.Source : -1));
            var available = layout.Available >= 0 ? Slice(line, layout.Available, layout.Source >= 0 ? layout.Source : -1) : "";
            var source = layout.Source >= 0 ? Slice(line, layout.Source, -1) : "";

            if (string.IsNullOrWhiteSpace(id)) continue;

            read.Add((i, new RawRow
            {
                Name = name,
                Id = id,
                Version = version,
                Available = available,
                Source = source
            }));
        }

        return [.. read.Select(r => r.Row)];
    }

    // The index of the second non-blank line above lines[separator]: the heading of the table that separator starts,
    // above its header.
    private static int SecondLineAbove(List<string> lines, int separator)
    {
        int found = 0;
        for (int i = separator - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) && ++found == 2) return i;
        }
        return 0;
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

    // Column titles winget prints, by column, in each language it ships: SearchId, SearchVersion, AvailableHeader
    // and SearchSource in its string tables, for en-US and the ten languages under Localization/Resources. Russian,
    // Traditional Chinese, the Korean Id and Source, and the Spanish and Portuguese Source were missing, so a
    // four-column list table, which has no positional fallback, ran Version on into Source (#2955). The other words
    // were in these lists before and are kept. A 4-column table is ambiguous by position alone (Name/Id/Version/Source
    // for `list` vs Name/Id/Version/Available for a source-less `upgrade`), so we identify each optional column by
    // matching its header title against these — never by its ordinal position. Lower-cased for comparison.
    private static readonly string[] IdTitles =
        ["id", "ид", "識別碼", "장치 id", "kennung", "identifiant", "identificador", "identificatore", "識別子", "标识符", "식별자"];
    private static readonly string[] VersionTitles =
        ["version", "versione", "versión", "versão", "версия", "バージョン", "版本", "버전"];
    private static readonly string[] AvailableTitles =
        ["available", "verfügbar", "disponible", "disponibile", "disponível", "доступно", "利用可能", "可用", "사용 가능"];
    private static readonly string[] SourceTitles =
        ["source", "quelle", "origen", "origine", "origem", "источник", "ソース", "源", "來源", "원본",
         "fuente", "fonte", "소스"];
    // "Match" (winget search) is not consumed by any caller; we don't need to localize it.

    // Column titles are localized (de-DE: "Name ID Version Verfügbar Quelle"), but the
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

    // Header tokens as (start column, word), a word being a run of non-blank text. Two words one space apart are one
    // token when together they are one title winget prints: Korean prints "장치 ID" for Id and "사용 가능" for
    // Available, which split on spaces put Id at "ID" and could never match (#2793, #2955).
    private static List<(int Start, string Word)> HeaderTokens(string header)
    {
        List<(int Start, int End, string Word)> words = [];
        int column = 0, wordColumn = -1, wordIndex = 0;
        for (int i = 0; i < header.Length;)
        {
            int length = StringInfo.GetNextTextElementLength(header.AsSpan(i));
            bool blank = char.IsWhiteSpace(header, i);
            if (!blank && wordColumn < 0) { wordColumn = column; wordIndex = i; }
            if (blank && wordColumn >= 0) { words.Add((wordColumn, column, header[wordIndex..i])); wordColumn = -1; }
            column += Cells(header.AsSpan(i, length));
            i += length;
        }
        if (wordColumn >= 0) words.Add((wordColumn, column, header[wordIndex..]));

        List<(int Start, string Word)> tokens = [];
        for (int w = 0; w < words.Count; w++)
        {
            var (start, end, word) = words[w];
            if (w + 1 < words.Count && words[w + 1].Start == end + 1
                && IsTitle($"{word} {words[w + 1].Word}".ToLowerInvariant()))
            {
                word = $"{word} {words[w + 1].Word}";
                w++;
            }
            tokens.Add((start, word));
        }
        return tokens;
    }

    private static bool IsTitle(string word) =>
        IdTitles.Contains(word) || VersionTitles.Contains(word) || AvailableTitles.Contains(word)
        || SourceTitles.Contains(word);

    // The text of a row between two columns, both in display columns, end -1 for the end of the line.
    private static string Slice(string line, int start, int end)
    {
        if (start < 0) return string.Empty;
        int from = IndexAtColumn(line, start);
        int to = end < 0 ? line.Length : IndexAtColumn(line, end);
        // Guard against out-of-order columns (start > end): winget can emit headers
        // whose detected positions don't increase monotonically, which would make the
        // range operator throw ArgumentOutOfRangeException.
        if (to <= from) return string.Empty;
        return line[from..to].Trim();
    }

    // The index in line of the text element at display column `column`, or line.Length when the line ends first. A
    // column that falls inside a wide character, which is never where winget starts one, starts after it.
    private static int IndexAtColumn(string line, int column)
    {
        int at = 0;
        for (int i = 0; i < line.Length;)
        {
            if (at >= column) return i;
            int length = StringInfo.GetNextTextElementLength(line.AsSpan(i));
            at += Cells(line.AsSpan(i, length));
            i += length;
        }
        return line.Length;
    }

    // The display columns a line takes, as winget counts them.
    private static int CellsOf(string line)
    {
        int cells = 0;
        for (int i = 0; i < line.Length;)
        {
            int length = StringInfo.GetNextTextElementLength(line.AsSpan(i));
            cells += Cells(line.AsSpan(i, length));
            i += length;
        }
        return cells;
    }

    // The display columns one text element takes, as winget counts them. Utility::UTF8ColumnWidth scores each text
    // element by its first code point, two when ICU calls it East Asian Wide or Fullwidth and one otherwise, and
    // TableOutput pads every field to its column with that width. Counted as characters, every column after a
    // Chinese, Japanese or Korean character, in a header or in a name, was read too far to the left (#2793).
    private static int Cells(ReadOnlySpan<char> element)
    {
        Rune.DecodeFromUtf16(element, out var rune, out _);
        return IsWide(rune.Value) ? 2 : 1;
    }

    private static bool IsWide(int codePoint)
    {
        int low = 0, high = WideRanges.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (codePoint < WideRanges[middle].First) high = middle - 1;
            else if (codePoint > WideRanges[middle].Last) low = middle + 1;
            else return true;
        }
        return false;
    }

    // The East Asian Wide and Fullwidth code points of Unicode 15.0 (EastAsianWidth.txt, W and F), as ranges.
    private static readonly (int First, int Last)[] WideRanges =
    [
        (0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23EC), (0x23F0, 0x23F0),
        (0x23F3, 0x23F3), (0x25FD, 0x25FE), (0x2614, 0x2615), (0x2648, 0x2653), (0x267F, 0x267F),
        (0x2693, 0x2693), (0x26A1, 0x26A1), (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5),
        (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5),
        (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728),
        (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797),
        (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55),
        (0x2E80, 0x2E99), (0x2E9B, 0x2EF3), (0x2F00, 0x2FD5), (0x2FF0, 0x2FFB), (0x3000, 0x303E),
        (0x3041, 0x3096), (0x3099, 0x30FF), (0x3105, 0x312F), (0x3131, 0x318E), (0x3190, 0x31E3),
        (0x31F0, 0x321E), (0x3220, 0x3247), (0x3250, 0x4DBF), (0x4E00, 0xA48C), (0xA490, 0xA4C6),
        (0xA960, 0xA97C), (0xAC00, 0xD7A3), (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE52),
        (0xFE54, 0xFE66), (0xFE68, 0xFE6B), (0xFF01, 0xFF60), (0xFFE0, 0xFFE6), (0x16FE0, 0x16FE4),
        (0x16FF0, 0x16FF1), (0x17000, 0x187F7), (0x18800, 0x18CD5), (0x18D00, 0x18D08), (0x1AFF0, 0x1AFF3),
        (0x1AFF5, 0x1AFFB), (0x1AFFD, 0x1AFFE), (0x1B000, 0x1B122), (0x1B132, 0x1B132), (0x1B150, 0x1B152),
        (0x1B155, 0x1B155), (0x1B164, 0x1B167), (0x1B170, 0x1B2FB), (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF),
        (0x1F18E, 0x1F18E), (0x1F191, 0x1F19A), (0x1F200, 0x1F202), (0x1F210, 0x1F23B), (0x1F240, 0x1F248),
        (0x1F250, 0x1F251), (0x1F260, 0x1F265), (0x1F300, 0x1F320), (0x1F32D, 0x1F335), (0x1F337, 0x1F37C),
        (0x1F37E, 0x1F393), (0x1F3A0, 0x1F3CA), (0x1F3CF, 0x1F3D3), (0x1F3E0, 0x1F3F0), (0x1F3F4, 0x1F3F4),
        (0x1F3F8, 0x1F43E), (0x1F440, 0x1F440), (0x1F442, 0x1F4FC), (0x1F4FF, 0x1F53D), (0x1F54B, 0x1F54E),
        (0x1F550, 0x1F567), (0x1F57A, 0x1F57A), (0x1F595, 0x1F596), (0x1F5A4, 0x1F5A4), (0x1F5FB, 0x1F64F),
        (0x1F680, 0x1F6C5), (0x1F6CC, 0x1F6CC), (0x1F6D0, 0x1F6D2), (0x1F6D5, 0x1F6D7), (0x1F6DC, 0x1F6DF),
        (0x1F6EB, 0x1F6EC), (0x1F6F4, 0x1F6FC), (0x1F7E0, 0x1F7EB), (0x1F7F0, 0x1F7F0), (0x1F90C, 0x1F93A),
        (0x1F93C, 0x1F945), (0x1F947, 0x1F9FF), (0x1FA70, 0x1FA7C), (0x1FA80, 0x1FA88), (0x1FA90, 0x1FABD),
        (0x1FABF, 0x1FAC5), (0x1FACE, 0x1FADB), (0x1FAE0, 0x1FAE8), (0x1FAF0, 0x1FAF8), (0x20000, 0x2FFFD),
        (0x30000, 0x3FFFD),
    ];
}
