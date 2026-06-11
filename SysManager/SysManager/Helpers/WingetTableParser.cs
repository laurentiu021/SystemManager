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

    /// <summary>
    /// Parses a winget table output into raw rows. The headerPattern identifies the
    /// header line, and summaryPattern identifies the termination line.
    /// </summary>
    internal static List<RawRow> Parse(
        List<string> lines,
        Regex headerPattern,
        Regex summaryPattern)
    {
        List<RawRow> rows = [];

        int headerIdx = lines.FindIndex(l => headerPattern.IsMatch(l));
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

    private static ColumnLayout DetectColumns(string header) => new()
    {
        Name = 0,
        Id = header.IndexOf("Id", StringComparison.OrdinalIgnoreCase),
        Version = header.IndexOf("Version", StringComparison.OrdinalIgnoreCase),
        Available = header.IndexOf("Available", StringComparison.OrdinalIgnoreCase),
        Source = header.IndexOf("Source", StringComparison.OrdinalIgnoreCase)
    };

    private static string Slice(string line, int start, int end)
    {
        if (start < 0 || start >= line.Length) return string.Empty;
        int actualEnd = end < 0 ? line.Length : Math.Min(end, line.Length);
        return line[start..actualEnd].Trim();
    }
}
