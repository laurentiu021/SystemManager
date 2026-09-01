// SysManager · AtomicFile
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// Writes a file so that a crash, power loss or full disk can never leave a half-written one behind.
/// <para>
/// <see cref="File.WriteAllText(string, string)"/> truncates the destination and then writes into it.
/// Interrupt that and the file on disk is neither the old contents nor the new — it is torn. That
/// matters here because SysManager's loaders treat a file they cannot parse as "no data": several
/// catch <see cref="System.Text.Json.JsonException"/> and substitute an empty list, at Debug level.
/// So an interrupted save does not surface as an error — it silently erases the user's activity
/// history, gaming profiles or volume presets.
/// </para>
/// <para>
/// The fix is to write a temporary file in the same directory, push it out of the operating system's
/// write-back cache onto the device, and then swap it in with a single filesystem operation. The flush is
/// what makes the power-loss half of the promise true: without it the rename can become durable while the
/// data blocks are still in volatile cache, leaving an empty file under the final name. <see cref="File.Replace(string, string, string?)"/> is used when the
/// destination already exists, because it preserves the destination's ACLs and attributes — a plain
/// <c>Move(overwrite: true)</c> relinks a brand-new inode that inherits only the directory's default
/// ACL, silently weakening the file. On first creation there is no descriptor to preserve, so a plain
/// <see cref="File.Move(string, string, bool)"/> is correct. This mirrors
/// <c>HostsFileService</c>, which already did it this way; the helper exists so the pattern is stated
/// once rather than copied into every service that persists user data.
/// </para>
/// </summary>
internal static class AtomicFile
{
    /// <summary>Distinguishes the temp files of overlapping writes; see <see cref="PrepareTempPath"/>.</summary>
    private static int _tempSequence;

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically, creating the
    /// directory if needed. The destination is either fully replaced or left exactly as it was.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        Write(path, temp => File.WriteAllText(temp, contents));
    }

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically using an explicit
    /// <paramref name="encoding"/>, for callers that must control the byte-order mark.
    /// </summary>
    public static void WriteAllText(string path, string contents, Encoding encoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(encoding);

        Write(path, temp => File.WriteAllText(temp, contents, encoding));
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> atomically, creating the directory
    /// if needed. The destination is either fully replaced or left exactly as it was.
    /// </summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);

        Write(path, temp => File.WriteAllBytes(temp, bytes));
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="WriteAllText(string, string)"/>. The swap itself stays
    /// synchronous: it is a single metadata operation, and there is no async <c>File.Replace</c>.
    /// </summary>
    public static async Task WriteAllTextAsync(
        string path, string contents, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        await WriteAsync(path, temp => File.WriteAllTextAsync(temp, contents, ct))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="WriteAllBytes(string, byte[])"/>.
    /// </summary>
    public static async Task WriteAllBytesAsync(
        string path, byte[] bytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);

        await WriteAsync(path, temp => File.WriteAllBytesAsync(temp, bytes, ct))
            .ConfigureAwait(false);
    }

    private static void Write(string path, Action<string> writeTemp)
    {
        var temp = PrepareTempPath(path);
        try
        {
            writeTemp(temp);
            FlushToDisk(temp);
            Swap(temp, path);
        }
        finally
        {
            CleanUp(temp);
        }
    }

    private static async Task WriteAsync(string path, Func<string, Task> writeTemp)
    {
        var temp = PrepareTempPath(path);
        try
        {
            await writeTemp(temp).ConfigureAwait(false);
            FlushToDisk(temp);
            Swap(temp, path);
        }
        finally
        {
            CleanUp(temp);
        }
    }

    /// <summary>
    /// A scratch path in <paramref name="path"/>'s own directory that no other writer can name, ending
    /// in <paramref name="tag"/>. Public to the assembly so every service that swaps a temp into place
    /// derives the name here rather than composing its own.
    /// <para>Uniqueness is load-bearing, not tidiness. A fixed <c>"&lt;path&gt;.tmp"</c> is shared by
    /// every writer to the same destination, and cleanup runs in a <c>finally</c> — so a second writer
    /// that fails to open the temp (the first still holds it) deletes it on the way out, and if the
    /// first has closed but not yet swapped, its swap then finds nothing. BOTH writes are lost and the
    /// destination never appears, silently, because callers log a failed save at Debug. With a name
    /// only one call knows, cleanup can only ever delete its own file and the last writer to swap
    /// simply wins.</para>
    /// <para>Deliberately NOT paired with a sweep of stale sibling temps: deleting a temp this call did
    /// not create is precisely the bug above. A leftover survives only a hard kill inside the window
    /// between writing the temp and swapping it, and costs a few hundred bytes.</para>
    /// </summary>
    public static string UniqueTempPath(string path, string tag = "tmp") =>
        $"{path}.{Environment.ProcessId}-{Interlocked.Increment(ref _tempSequence)}.{tag}";

    private static string PrepareTempPath(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Same directory as the destination: a cross-volume move is a copy+delete, which is not
        // atomic and would reintroduce exactly the torn window this helper exists to close.
        return UniqueTempPath(path);
    }

    /// <summary>
    /// Pushes the temp file onto the device before the swap turns it into the destination.
    /// </summary>
    /// <remarks>
    /// Closing a handle — which <c>File.WriteAllText</c> and its siblings do — hands the bytes to the
    /// operating system's write-back cache. It does not ask the drive to persist them. The swap that
    /// follows is a metadata change NTFS journals, so the rename can become durable while the data blocks
    /// are still only in volatile cache: after a power cut the destination exists under its final name and
    /// is empty or torn. That is the one outcome this class exists to prevent, and it is the quietest,
    /// because the loaders reading these files treat an unparseable file as "no data" at Debug level.
    /// <para>Stronger than <see cref="FileOptions.WriteThrough"/>, which bypasses the OS cache but leaves
    /// the drive's own cache alone. This issues FlushFileBuffers, which commits it.</para>
    /// <para>A device that cannot flush must not cost the caller a write that would otherwise have
    /// completed — some network and virtual volumes refuse the call outright — so the failure is logged
    /// and the swap proceeds. The result is then exactly the behaviour that shipped before this existed,
    /// never a new exception.</para>
    /// </remarks>
    private static void FlushToDisk(string temp)
    {
        try
        {
            using var handle = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None);
            handle.Flush(flushToDisk: true);
        }
        catch (IOException ex)
        {
            Log.Debug(ex, "Could not flush {Temp} onto the device; swapping it in regardless", temp);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug(ex, "Could not open {Temp} to flush it; swapping it in regardless", temp);
        }
    }

    private static void Swap(string temp, string path)
    {
        if (File.Exists(path))
            File.Replace(temp, path, destinationBackupFileName: null);
        else
            MoveIntoPlace(temp, path);
    }

    /// <summary>
    /// The first-creation swap: with no destination descriptor to preserve, a plain move is correct.
    /// </summary>
    /// <remarks>
    /// <see cref="Swap"/> asks whether the destination exists and then acts on the answer, and those are
    /// two operations rather than one. Two writers saving a file that does not exist yet — the first save
    /// of a preset, profile or history — can both be told "absent", and the loser's
    /// <c>Move(overwrite: false)</c> then throws because the winner got there first. Callers log a failed
    /// save at Debug, so that writer's data would disappear without a word. Every subsequent write is
    /// already safe, since it takes the <see cref="File.Replace(string, string, string?)"/> branch.
    /// <para>The fallback is that same replace rather than <c>Move(overwrite: true)</c>, which would
    /// relink a new inode inheriting only the directory's default ACL — the thing this class's summary
    /// argues against. It also makes the last writer win, which is the contract
    /// <see cref="UniqueTempPath"/> already states.</para>
    /// <para>Internal so a test can drive the state the race produces without depending on timing.</para>
    /// </remarks>
    internal static void MoveIntoPlace(string temp, string path)
    {
        try
        {
            File.Move(temp, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Replace(temp, path, destinationBackupFileName: null);
        }
    }

    private static void CleanUp(string temp)
    {
        // The temp file survives only if the swap never happened. Deleting it is best-effort: failing
        // to clean up a leftover must not become the exception the caller sees, which would hide the
        // real write failure.
        if (!File.Exists(temp)) return;

        try { File.Delete(temp); }
        catch (IOException) { /* leftover temp file; the destination is intact either way */ }
        catch (UnauthorizedAccessException) { }
    }
}
