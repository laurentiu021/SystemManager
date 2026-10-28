// SysManager · Csv
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Text;

namespace SysManager.Helpers;

/// <summary>
/// Builds and writes CSV a spreadsheet will read back correctly, in the region of the person who opens it.
/// </summary>
/// <remarks>
/// The exports carry app names, setting descriptions and folder paths — text a user chose or Windows chose, and
/// <c>C:\Users\me\Music\Grieg, Peer Gynt</c> is an ordinary folder name that silently becomes two columns without
/// quoting. So the escaping lives here once rather than being re-derived per tab.
/// <para>The separator and the numbers follow the culture each export is given, which is the user's
/// <see cref="CultureInfo.CurrentCulture"/> when a tab saves a file. Excel, opening a CSV by double-click, splits it
/// on Windows' list separator and reads numbers with the regional decimal mark, and in every region that writes
/// decimals with a comma, Romanian included, that separator is a semicolon. Written with commas and dots, every row
/// opened as one cell (#2888). The invariant culture writes the commas and dots of RFC 4180.</para>
/// <para>A field is quoted when it holds the separator, a quote, CR or LF, and an embedded quote is doubled. Nothing
/// else is altered — no trimming, no separator substitution — because a lossy export of a path is worse than a
/// quoted one.</para>
/// <para>A file is written as UTF-8 with a byte order mark, which Excel needs to read it as UTF-8 rather than in the
/// ANSI code page, where "é" came out as "Ã©" and an em dash as three characters (#2794).</para>
/// <para>A field starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return is read by a
/// spreadsheet as a formula rather than text (OWASP's CSV injection guidance) — a risk several exports carry
/// without controlling it: file and folder names (Windows allows one named <c>=1+2.txt</c>), and program, window
/// and publisher names. Such a field is prefixed with a single quote, which a spreadsheet shows literally rather
/// than evaluating, UNLESS the field is a genuine number in the sheet's culture (a legitimate <c>-5</c> or
/// <c>+5</c>), which is left untouched so a numeric column still sorts and totals correctly (#2794).</para>
/// </remarks>
public static class Csv
{
    /// <summary>The characters other than the separator that force a field to be quoted.</summary>
    private static readonly char[] QuoteOrLineBreak = ['"', '\r', '\n'];

    /// <summary>The leading characters a spreadsheet reads as the start of a formula rather than text.</summary>
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>UTF-8 with the byte order mark, the encoding every CSV is written in.</summary>
    private static readonly UTF8Encoding Utf8WithMark = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// The separator a spreadsheet in <paramref name="culture"/> splits fields on: its list separator, or a comma when
    /// it has none.
    /// </summary>
    public static string SeparatorOf(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var separator = culture.TextInfo.ListSeparator;
        return string.IsNullOrEmpty(separator) ? "," : separator;
    }

    /// <summary>
    /// One CSV field for a sheet in <paramref name="culture"/>, quoted only when it has to be. A null becomes an empty
    /// field. A value a spreadsheet would read as a formula is prefixed with a single quote first, unless it is a
    /// genuine number in <paramref name="culture"/>.
    /// </summary>
    public static string Field(string? value, CultureInfo culture)
    {
        var separator = SeparatorOf(culture);
        if (string.IsNullOrEmpty(value)) return "";

        var text = NeedsFormulaGuard(value, culture) ? "'" + value : value;

        if (!text.Contains(separator, StringComparison.Ordinal) && text.IndexOfAny(QuoteOrLineBreak) < 0)
            return text;
        return string.Concat("\"", text.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
    }

    /// <summary>
    /// True when <paramref name="value"/> starts with a character a spreadsheet reads as the start of a formula,
    /// and is not itself a genuine number in <paramref name="culture"/>.
    /// </summary>
    /// <remarks>
    /// <c>=</c> and <c>@</c> never start a valid number, so those are always guarded. <c>+</c> and <c>-</c> do — a
    /// column of signed totals or temperatures must not have its sign quoted away — so those two are guarded only
    /// when the whole field fails to parse as a number in the sheet's own culture, meaning it is text that merely
    /// starts with the sign, such as a filename or an actual formula.
    /// </remarks>
    private static bool NeedsFormulaGuard(string value, CultureInfo culture)
    {
        if (Array.IndexOf(FormulaTriggers, value[0]) < 0) return false;
        if (value[0] != '+' && value[0] != '-') return true;
        return !double.TryParse(value, NumberStyles.Float, culture, out _);
    }

    /// <summary>
    /// One CSV row for a sheet in <paramref name="culture"/>, each field escaped, terminated with CRLF.
    /// </summary>
    /// <remarks>
    /// CRLF rather than <c>AppendLine</c>'s platform default: this is a file format, not console output, and
    /// RFC 4180 names CRLF. It also keeps the bytes identical if the export is ever produced somewhere other
    /// than Windows.
    /// </remarks>
    public static void AppendRow(StringBuilder builder, CultureInfo culture, params string?[] fields)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(fields);
        var separator = SeparatorOf(culture);

        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) builder.Append(separator);
            builder.Append(Field(fields[i], culture));
        }
        builder.Append("\r\n");
    }

    /// <summary>Writes <paramref name="csv"/> to <paramref name="path"/> as UTF-8 with a byte order mark.</summary>
    public static void Write(string path, string csv) => File.WriteAllText(path, csv, Utf8WithMark);

    /// <summary>Writes <paramref name="csv"/> to <paramref name="path"/> as UTF-8 with a byte order mark.</summary>
    public static Task WriteAsync(string path, string csv, CancellationToken ct = default) =>
        File.WriteAllTextAsync(path, csv, Utf8WithMark, ct);
}
