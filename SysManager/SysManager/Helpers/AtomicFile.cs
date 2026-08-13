// SysManager · AtomicFile
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text;

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
/// The fix is to write a temporary file in the same directory and then swap it in with a single
/// filesystem operation. <see cref="File.Replace(string, string, string?)"/> is used when the
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
            Swap(temp, path);
        }
        finally
        {
            CleanUp(temp);
        }
    }

    private static string PrepareTempPath(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Same directory as the destination: a cross-volume move is a copy+delete, which is not
        // atomic and would reintroduce exactly the torn window this helper exists to close.
        return path + ".tmp";
    }

    private static void Swap(string temp, string path)
    {
        if (File.Exists(path))
            File.Replace(temp, path, destinationBackupFileName: null);
        else
            File.Move(temp, path, overwrite: false);
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
