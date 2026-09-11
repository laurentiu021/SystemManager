// SysManager · Csv
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text;

namespace SysManager.Helpers;

/// <summary>
/// Builds CSV a spreadsheet will read back correctly, including fields that contain the separator.
/// </summary>
/// <remarks>
/// <see cref="Services.ResourceHistoryService.ToCsv"/> came first and writes its fields raw, which is safe
/// there because every one of them is a number or a fixed-format timestamp. The exports that followed carry
/// app names, setting descriptions and folder paths — text a user chose or Windows chose, and
/// <c>C:\Users\me\Music\Grieg, Peer Gynt</c> is an ordinary folder name that silently becomes two columns
/// without quoting. So the escaping lives here once rather than being re-derived per tab.
/// <para>RFC 4180: a field is quoted when it contains a comma, a quote, CR or LF, and an embedded quote is
/// doubled. Nothing else is altered — no trimming, no separator substitution — because a lossy export of a
/// path is worse than a quoted one.</para>
/// </remarks>
public static class Csv
{
    /// <summary>The characters that force a field to be quoted.</summary>
    private static readonly char[] MustQuote = [',', '"', '\r', '\n'];

    /// <summary>One CSV field, quoted only when it has to be. A null becomes an empty field.</summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.IndexOfAny(MustQuote) < 0) return value;
        return string.Concat("\"", value.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
    }

    /// <summary>
    /// One CSV row, each field escaped, terminated with CRLF.
    /// </summary>
    /// <remarks>
    /// CRLF rather than <c>AppendLine</c>'s platform default: this is a file format, not console output, and
    /// RFC 4180 names CRLF. It also keeps the bytes identical if the export is ever produced somewhere other
    /// than Windows.
    /// </remarks>
    public static void AppendRow(StringBuilder builder, params string?[] fields)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(fields);

        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(Field(fields[i]));
        }
        builder.Append("\r\n");
    }
}
