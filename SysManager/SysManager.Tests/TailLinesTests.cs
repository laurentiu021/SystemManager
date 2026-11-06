// SysManager · TailLinesTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Linq;
using System.Text;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// <see cref="TailLines"/> gives a file's lines last first, the lines <see cref="File.ReadAllLines(string)"/>
/// gives, and reads only as far back as it is asked (#2853).
/// </summary>
public class TailLinesTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "SysManagerTailLinesTests", Guid.NewGuid().ToString("N"));

    public TailLinesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
        GC.SuppressFinalize(this);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// The files a history file can be: LF lines as an append writes them, CRLF lines as a prune writes them, both,
    /// and the edges.
    /// </summary>
    public static TheoryData<string, byte[]> Files() => new()
    {
        { "empty", [] },
        { "one line with no ending", Utf8("a") },
        { "an LF alone", Utf8("\n") },
        { "a CRLF alone", Utf8("\r\n") },
        { "LF lines", Utf8("{\"t\":1}\n{\"t\":2}\n{\"t\":3}\n") },
        { "CRLF lines", Utf8("{\"t\":1}\r\n{\"t\":2}\r\n") },
        { "CRLF lines, then LF lines", Utf8("{\"t\":1}\r\n{\"t\":2}\r\n{\"t\":3}\n{\"t\":4}\n") },
        { "no ending at the end", Utf8("a\nb\r\nc") },
        { "empty lines", Utf8("a\n\n\r\nb\n") },
        { "a byte order mark, then lines", [.. Encoding.UTF8.Preamble, .. Utf8("a\nb\n")] },
        { "a byte order mark alone", [.. Encoding.UTF8.Preamble] },
        { "a byte order mark and an LF", [.. Encoding.UTF8.Preamble, .. Utf8("\n")] },
        { "characters of two, three and four bytes", Utf8("\u0103 \u00B0C\n\u20AC\r\n\U0001F600\n") },
    };

    /// <summary>
    /// Every read size from one byte up, so a line, a CRLF and every character fall across a cut between two reads.
    /// </summary>
    [Theory]
    [MemberData(nameof(Files))]
    public async Task TheLines_AreTheOnesAWholeFileReadGives_LastFirst_WhereverTheReadsCut(string file, byte[] content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".ndjson");
        File.WriteAllBytes(path, content);
        var expected = File.ReadAllLines(path).Reverse().ToList();

        for (var chunk = 1; chunk <= content.Length + 1; chunk++)
            Assert.True(expected.SequenceEqual(await All(path, chunk)), $"{file}, read {chunk} bytes at a time");
        Assert.Equal(expected, await All(path, TailLines.ChunkBytes));
    }

    /// <summary>
    /// Only as far back as it is asked: with the start of a long file locked against reading, its last line still
    /// reads. A read of the whole file fails on the lock, which is how a load that reads every line is caught.
    /// </summary>
    [Fact]
    public async Task ItReadsOnlyAsFarBackAsItIsAsked()
    {
        var count = 3 * TailLines.ChunkBytes / 11 + 1;   // lines of 11 bytes, three reads' worth
        var path = Path.Combine(_dir, "long.ndjson");
        File.WriteAllText(path, string.Concat(Enumerable.Range(0, count).Select(i => $"line {i:D5}\n")));

        using var head = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        head.Lock(0, TailLines.ChunkBytes);
        Assert.Throws<IOException>(() => File.ReadAllLines(path));   // the lock holds

        await using var lines = TailLines.ReadBackwardAsync(path).GetAsyncEnumerator();
        Assert.True(await lines.MoveNextAsync());
        Assert.Equal($"line {count - 1:D5}", lines.Current);
    }

    [Fact]
    public async Task ACancelledRead_ReadsNothing()
    {
        var path = Path.Combine(_dir, "two.ndjson");
        File.WriteAllText(path, "a\nb\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => All(path, TailLines.ChunkBytes, cts.Token));
    }

    private static async Task<List<string>> All(string path, int chunk, CancellationToken ct = default)
    {
        var lines = new List<string>();
        await foreach (var line in TailLines.ReadBackwardAsync(path, ct, chunk)) lines.Add(line);
        return lines;
    }
}
