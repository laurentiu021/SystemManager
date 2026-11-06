// SysManager · TailLines
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SysManager.Helpers;

/// <summary>
/// A text file's lines from the last to the first, read from the file's end as they are asked for, so the newest lines
/// of a long file cost those lines rather than the whole file.
/// </summary>
/// <remarks>
/// The two history files are appended oldest-first, and a load wants the newest samples, back to the start of its
/// range. Both loads read the whole file with <see cref="File.ReadAllLinesAsync(string, CancellationToken)"/> and
/// walked back from its end, so the last hour of a 30-day Resource History file read and allocated all of its 260,000
/// lines (#2853).
/// <para>The lines are the ones <see cref="File.ReadAllLines(string)"/> gives, last first, for a UTF-8 file whose
/// lines end in LF, as an append writes them, or CRLF, as a prune's rewrite writes them. Neither ending is part of a
/// line, an ending at the end of the file starts no empty line, and a byte order mark is not part of the first line.
/// The file is cut only at an LF, a byte that is never part of a longer character, and each line is decoded
/// whole.</para>
/// </remarks>
internal static class TailLines
{
    /// <summary>How much of the file one read takes: about 600 history samples.</summary>
    internal const int ChunkBytes = 64 * 1024;

    /// <summary>
    /// Reads the lines of <paramref name="path"/>, last first, for as long as the caller goes on asking.
    /// </summary>
    /// <param name="path">
    /// The file, opened for reading and shared for reading only, as a whole-file read opens it.
    /// </param>
    /// <param name="ct">Ends the read at the next read of the file.</param>
    /// <param name="chunkBytes">
    /// How much one read takes. A test passes a few bytes, so a line, a line ending and a character all fall across a
    /// cut between two reads.
    /// </param>
    public static async IAsyncEnumerable<string> ReadBackwardAsync(
        string path, [EnumeratorCancellation] CancellationToken ct = default, int chunkBytes = ChunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.Asynchronous);
        var length = RandomAccess.GetLength(file);
        if (length == 0) yield break;

        var end = length;
        var chunk = new byte[(int)Math.Min(chunkBytes, end)];
        await ReadExactlyAsync(file, chunk.AsMemory(0, 1), end - 1, ct).ConfigureAwait(false);
        if (chunk[0] == (byte)'\n') end--;   // the file's last line ending starts no line after it

        var after = Array.Empty<byte>();   // the end of the line being read, from the reads after this one
        while (end > 0)
        {
            var count = (int)Math.Min(chunk.Length, end);
            var start = end - count;
            await ReadExactlyAsync(file, chunk.AsMemory(0, count), start, ct).ConfigureAwait(false);

            var lineEnd = count;   // where, in this read, the line being read stops
            for (var i = count - 1; i >= 0; i--)
            {
                if (chunk[i] != (byte)'\n') continue;
                yield return Decode(chunk, i + 1, lineEnd, after, firstLine: false);
                after = [];
                lineEnd = i;
            }
            after = Prepend(chunk, lineEnd, after);
            end = start;
        }

        // A file that holds a byte order mark and nothing else has no lines, as File.ReadAllLines gives it none.
        if (length == Encoding.UTF8.Preamble.Length && IsByteOrderMark(after)) yield break;
        yield return Decode(after, 0, after.Length, [], firstLine: true);
    }

    private static bool IsByteOrderMark(byte[] bytes) => bytes.AsSpan().SequenceEqual(Encoding.UTF8.Preamble);

    private static async ValueTask ReadExactlyAsync(SafeFileHandle file, Memory<byte> into, long offset,
                                                    CancellationToken ct)
    {
        while (!into.IsEmpty)
        {
            var read = await RandomAccess.ReadAsync(file, into, offset, ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The file got shorter while it was read.");
            into = into[read..];
            offset += read;
        }
    }

    /// <summary>
    /// The first <paramref name="count"/> bytes of <paramref name="chunk"/>, then <paramref name="after"/>.
    /// </summary>
    private static byte[] Prepend(byte[] chunk, int count, byte[] after) => [.. chunk.AsSpan(0, count), .. after];

    /// <summary>One line: <paramref name="chunk"/> from <paramref name="from"/> to <paramref name="to"/>, then
    /// <paramref name="after"/>, without its CR or, on the first line, a byte order mark.</summary>
    private static string Decode(byte[] chunk, int from, int to, byte[] after, bool firstLine)
    {
        ReadOnlySpan<byte> line = after.Length == 0
            ? chunk.AsSpan(from, to - from)
            : [.. chunk.AsSpan(from, to - from), .. after];
        if (firstLine && line.StartsWith(Encoding.UTF8.Preamble)) line = line[Encoding.UTF8.Preamble.Length..];
        if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
        return Encoding.UTF8.GetString(line);
    }
}
