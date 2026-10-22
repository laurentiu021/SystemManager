// SysManager · HostsFileService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads, parses, and writes the Windows hosts file with backup support.
/// </summary>
public sealed partial class HostsFileService
{
    private static readonly string DefaultHostsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "drivers", "etc", "hosts");

    private readonly string HostsPath;
    private readonly string BackupPath;

    /// <summary>
    /// Serializes <see cref="SaveHosts"/> against itself and against <see cref="RestoreBackup"/>. Both
    /// rewrite the WHOLE hosts file, and <see cref="SaveHosts"/> first reads it back, keeping the user's
    /// hand-written comments through <see cref="PreservedLines"/> to re-emit them. The staged write and
    /// the swap are each atomic — <see cref="AtomicFile.SwapIntoPlace"/> owns that — but the
    /// read-then-write PAIR is not: without this, whichever saves last swaps in a file built from
    /// content it captured before the other landed, and the other save's entries and comments are gone.
    /// The unique temp names already here stop two writers from deleting each other's staged file; they
    /// do not stop the second swap from overwriting the first result.
    /// <para>It also covers the back-up-only-once decision: two concurrent first saves would both find
    /// no backup, and the loser's <c>File.Copy(overwrite: false)</c> throws an
    /// <see cref="IOException"/> that nothing here handles.</para>
    /// <para>Latent rather than reachable today, so this is a guard rather than a user-facing fix. The
    /// tab's two commands are async <c>[RelayCommand]</c>s, which refuse to re-enter themselves, so two
    /// saves cannot overlap from the UI; a save against a restore CAN, because they are separate commands
    /// and the modal confirm only covers the prompt, not the off-thread write. The service surface is
    /// public either way, and a second caller must not be the thing that discovers this.</para>
    /// <para>Fifth store to need this, after <see cref="UpdateCheckPreferenceService"/>,
    /// <see cref="ServiceStartupLedgerService"/>, <see cref="VolumePresetService"/> and
    /// <see cref="NotificationBlockerService"/>;
    /// <c>ArchitectureTests.EveryStoreThatReadsThenWritesTheSameFile_SerializesThePair</c> now keeps the
    /// class closed. <see cref="ReadHostsAsync"/> is deliberately NOT gated: it only reads, and the swap
    /// means it sees either the whole previous file or the whole next one, never a torn mix.</para>
    /// </summary>
    private readonly Lock _writeLock = new();

    // SysManager's own managed-header lines. Kept as constants because SaveHosts both EMITS them
    // and must EXCLUDE them when preserving the user's standalone comments — otherwise the header
    // would be recaptured and re-emitted on every save, growing without bound (a singleton service
    // rewrites the whole file each time). One source of truth keeps emit and capture in lock-step.
    private const string ManagedHeaderLine1 = "# This file is managed by SysManager";
    private const string ManagedHeaderLine2 = "# Entries marked with # at the start are disabled";

    /// <summary>
    /// Creates the service against the real system hosts file. The optional
    /// <paramref name="hostsPath"/> override exists for testing so the backup /
    /// restore logic can be exercised without touching System32 or needing admin.
    /// </summary>
    public HostsFileService(string? hostsPath = null)
    {
        HostsPath = hostsPath ?? DefaultHostsPath;
        BackupPath = HostsPath + ".bak";
    }

    // One or more DNS labels separated by single dots. Each label is 1–63 chars,
    // alphanumeric with internal hyphens only (no leading/trailing hyphen). This
    // rejects consecutive dots ("a..b"), a leading/trailing dot, and over-long labels
    // that the previous looser pattern accepted.
    [GeneratedRegex(@"\A(?=.{1,253}\z)[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)*\z")]
    private static partial Regex HostnameRegex();

    /// <summary>
    /// Reads and parses the hosts file. Skips pure comment lines (starting with #
    /// that don't have an IP pattern). Detects commented-out entries as disabled.
    /// </summary>
    public async Task<List<HostsEntry>> ReadHostsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(HostsPath)) return [];
        var (lines, _) = Decode(await File.ReadAllBytesAsync(HostsPath, ct).ConfigureAwait(false));
        return ParseEntries(lines);
    }

    /// <summary>The entries in <paramref name="lines"/>, as <see cref="ReadHostsAsync"/> reads them.</summary>
    private static List<HostsEntry> ParseEntries(IEnumerable<string> lines)
    {
        List<HostsEntry> entries = [];
        foreach (string line in lines.Select(l => l.Trim()))
        {
            if (string.IsNullOrEmpty(line)) continue;

            bool isDisabled = false;
            string workLine = line;

            // Check if this is a commented-out entry (# followed by IP pattern)
            if (line.StartsWith('#'))
            {
                workLine = line[1..].TrimStart();
                // If the remainder isn't a valid disabled entry (IP + hostname), skip it
                if (!IsEntryLine(workLine)) continue;
                isDisabled = true;
            }

            // Parse: IP  hostname  [# comment]
            string[] parts = workLine.Split(['#'], 2);
            string entryPart = parts[0].Trim();
            string comment = parts.Length > 1 ? parts[1].Trim() : "";

            string[] tokens = entryPart.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;

            string ip = tokens[0];

            // Validate IP
            if (!IPAddress.TryParse(ip, out _)) continue;

            // A single hosts line can map one IP to several hostnames
            // (e.g. "127.0.0.1  a  b  c"). Emit one entry per hostname so none are
            // silently dropped on a read→save round trip.
            for (int t = 1; t < tokens.Length; t++)
            {
                entries.Add(new HostsEntry
                {
                    IpAddress = ip,
                    Hostname = tokens[t],
                    Comment = comment,
                    IsEnabled = !isDisabled
                });
            }
        }

        return entries;
    }

    /// <summary>
    /// Extracts every line of the current hosts file that <see cref="SaveHosts"/> would otherwise
    /// lose, so it can re-emit them.
    /// </summary>
    /// <remarks>
    /// SaveHosts rewrites the whole file from header + these lines + <see cref="HostsEntry"/> objects,
    /// so anything neither emitted as an entry nor captured here is deleted. Three kinds are captured:
    /// <list type="bullet">
    /// <item>Pure comments — a '#' line that is not a commented-out IP entry (those round-trip as
    /// disabled entries) and not one of SysManager's own managed-header lines (excluding those keeps
    /// repeated saves from accumulating duplicate headers).</item>
    /// <item>Lines that are not comments and do not parse as an entry: a typo'd IP, an IP with no
    /// hostname, a stray word. <see cref="ReadHostsAsync"/> skips them, so nothing else carries them.
    /// They are usually a line the user meant to work, which makes them worth keeping rather than
    /// quietly discarding — the malformed text is the record of the intent.</item>
    /// <item>Interior blank lines, for readability. Leading and trailing blanks are trimmed.</item>
    /// </list>
    /// The keep-or-skip decision uses <see cref="IsEntryLine"/>, the same predicate ReadHostsAsync
    /// uses to accept a line. That shared test is what keeps the two in step: a line this method keeps
    /// is exactly a line ReadHostsAsync did not turn into an entry, so nothing is duplicated and
    /// nothing is dropped. Returns an empty list when the file is absent, so a file of plain entries
    /// produces output byte-identical to the earlier behaviour.
    /// <para>Handed the lines <see cref="SaveHosts"/> read, in the file's own encoding. A file that is there and
    /// could not be read throws in that read, before anything is written. It used to read as "nothing to keep", so a
    /// save during a lock dropped every comment and every line the parser could not read (#2521).</para>
    /// </remarks>
    private static List<string> PreservedLines(IEnumerable<string> rawLines)
    {
        List<string> preserved = [];

        // Trim in the projection: the loop body never needs the untrimmed line.
        foreach (var line in rawLines.Select(r => r.Trim()))
        {

            if (line.Length == 0)
            {
                // Keep blank lines only once we've started collecting content, so leading blanks
                // (and the gap under our header) are dropped; trailing blanks are trimmed below.
                if (preserved.Count > 0) preserved.Add("");
                continue;
            }

            if (!line.StartsWith('#'))
            {
                // Carried by `entries` only if ReadHostsAsync could actually parse it. When it could
                // not, this is the last chance to keep the line, so keep it verbatim.
                if (IsEntryLine(line)) continue;
                preserved.Add(line);
                continue;
            }

            if (line == ManagedHeaderLine1 || line == ManagedHeaderLine2) continue; // our own header

            // A '#' followed by a valid disabled entry (IP + hostname) is already represented as a
            // HostsEntry with IsEnabled=false — skip it here so it isn't duplicated.
            if (IsEntryLine(line[1..].TrimStart())) continue;

            preserved.Add(line);
        }

        // Trim trailing blank lines so the preserved block ends cleanly (fixed point).
        while (preserved.Count > 0 && preserved[^1].Length == 0)
            preserved.RemoveAt(preserved.Count - 1);

        return preserved;
    }

    // Valid UTF-8 or nothing: a byte that is not throws, which is how a file saved in an ANSI code page is told apart.
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The lines of a hosts file, and the encoding it is in: that of its byte order mark, UTF-8 when every byte is
    /// valid UTF-8, and otherwise the system's ANSI code page, which older Notepad and many localised tools save in.
    /// </summary>
    /// <remarks>
    /// The file was read and written as UTF-8 whatever it was in, so in a file saved in an ANSI code page every letter
    /// outside ASCII was read as U+FFFD and written back as that, and a comment such as "# für Spiele" was destroyed by
    /// the first save (#2797). <see cref="SaveHosts"/> writes the file back in the encoding read here, with the same
    /// byte order mark or none. Split into lines as <see cref="File.ReadAllLines(string)"/> splits them.
    /// </remarks>
    internal static (List<string> Lines, Encoding Encoding) Decode(byte[] bytes)
    {
        ReadOnlySpan<byte> content = bytes;
        Encoding encoding;
        if (content.StartsWith(Encoding.UTF8.Preamble))
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            content = content[3..];
        }
        else if (content.StartsWith(Encoding.Unicode.Preamble))
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
            content = content[2..];
        }
        else if (content.StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
            content = content[2..];
        }
        else
        {
            try
            {
                _ = StrictUtf8.GetCharCount(content);
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
            catch (DecoderFallbackException)
            {
                encoding = PowerShellRunner.AnsiEncoding;
            }
        }

        List<string> lines = [];
        using var reader = new StringReader(encoding.GetString(content));
        while (reader.ReadLine() is { } line) lines.Add(line);
        return (lines, encoding);
    }

    /// <summary>
    /// The entries in a form that compares equal exactly when they are the same entries in the same order. Save writes
    /// only while the file still holds the entries the list was read from (#2797).
    /// </summary>
    public static string Fingerprint(IEnumerable<HostsEntry> entries) =>
        string.Join("\n", entries.Select(e => $"{e.IpAddress}\t{e.Hostname}\t{e.Comment}\t{(e.IsEnabled ? 1 : 0)}"));

    /// <summary>
    /// The question before the hosts file is put back from the copy beside it, made on <paramref name="madeOn"/>:
    /// on DNS &amp; Hosts' Restore original and on Undo Changes alike.
    /// </summary>
    public static string RestoreQuestion(string madeOn) =>
        $"Restore the hosts file from the copy beside it, made on {madeOn}?\n\nEverything written to it since — by "
        + "SysManager or by any other program — is replaced by that copy.";

    /// <summary>True if a copy of the hosts file is kept beside it as hosts.bak, whoever made it.</summary>
    public bool HasBackup => File.Exists(BackupPath);

    /// <summary>
    /// Whether the hosts file still differs from the copy kept beside it, whether SysManager has written the file, and
    /// when that copy was made, or null when there is no copy (#1525).
    /// </summary>
    /// <remarks>
    /// The copy is never deleted, so "there is a copy" stays true after the file has been put back. Undo Changes
    /// offers it only while the two differ: a row that is still there after its change went back would say there was
    /// something left to undo.
    /// <para>Nor is the copy always SysManager's. <see cref="SaveHosts"/> makes it only when there is none, so a
    /// <c>hosts.bak</c> another program or the user left there stays, and a file SysManager never wrote has no change
    /// of SysManager's in it. Only a file that starts with the header <see cref="SaveHosts"/> writes counts as one
    /// SysManager wrote.</para>
    /// <para>Compared by length first, then in chunks, so a hosts file of a few hundred thousand blocked sites is not
    /// read whole twice. Not gated: it only reads, and both writers swap a finished file into place, so it sees either
    /// file whole. A file that is there and could not be read throws, because one that cannot be compared is not the
    /// same.</para>
    /// </remarks>
    public HostsBackupState? ReadBackupState()
    {
        if (!File.Exists(BackupPath)) return null;
        var takenAtUtc = File.GetCreationTimeUtc(BackupPath);
        return new HostsBackupState(!SameContent(BackupPath, HostsPath), StartsWithOurHeader(HostsPath), takenAtUtc);
    }

    private static bool StartsWithOurHeader(string path)
    {
        if (!File.Exists(path)) return false;
        using var reader = new StreamReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return string.Equals(reader.ReadLine(), ManagedHeaderLine1, StringComparison.Ordinal);
    }

    private static bool SameContent(string first, string second)
    {
        if (!File.Exists(second)) return false;
        using var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (a.Length != b.Length) return false;

        var left = new byte[64 * 1024];
        var right = new byte[left.Length];
        while (true)
        {
            var read = a.ReadAtLeast(left, left.Length, throwOnEndOfStream: false);
            if (read == 0) return true;
            if (b.ReadAtLeast(right.AsSpan(0, read), read, throwOnEndOfStream: false) != read) return false;
            if (!left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, read))) return false;
        }
    }

    /// <summary>
    /// Saves entries back to the hosts file. Disabled entries are written as commented lines.
    /// </summary>
    /// <remarks>
    /// The backup is created ONLY the first time (when no backup yet exists), so it
    /// preserves the original pre-SysManager hosts file. Previously the backup was
    /// overwritten on every save, which meant that after the first save the ".bak"
    /// already contained SysManager's own output — losing the pristine original and
    /// defeating <see cref="RestoreBackup"/>.
    ///
    /// Anything the user wrote that the parser does not turn into an entry is preserved verbatim
    /// above the entries: standalone comments, blank lines, and lines that fail to parse at all
    /// (a typo'd IP, an IP with no hostname). Without that, editing one entry through the UI
    /// silently deleted the rest of the file's hand-written content. SysManager's own managed-header
    /// lines are excluded from the capture, making the rewrite a fixed point — repeated saves don't
    /// accumulate duplicate headers or blanks. See <see cref="PreservedLines"/>.
    ///
    /// The whole body is inside <see cref="_writeLock"/>, because the rewrite is built from content
    /// this method just read: the capture and the swap have to be one step or a concurrent save's
    /// entries are dropped. Not <c>async</c> and no <c>await</c>, so the lock is never held across a
    /// yield.
    /// <para>The list was read from the file when the tab opened, and SysManager keeps running in the notification
    /// area, so it can be days old. Rebuilt from it, the file lost every mapping a VPN client, Docker Desktop, an
    /// ad-blocker or an edit in Notepad had made since (#2797). With <paramref name="loadedFingerprint"/>, the
    /// <see cref="Fingerprint"/> of the entries the list was read from, nothing is written unless the file still holds
    /// those entries. The file is written back in the encoding it was read in (<see cref="Decode"/>).</para>
    /// </remarks>
    /// <param name="entries">The entries to write.</param>
    /// <param name="loadedFingerprint">
    /// The <see cref="Fingerprint"/> of the entries the list was read from, or null to write whatever the file holds.
    /// </param>
    public HostsSaveOutcome SaveHosts(List<HostsEntry> entries, string? loadedFingerprint = null)
    {
        lock (_writeLock)
        {
            var exists = File.Exists(HostsPath);
            var (current, encoding) = exists
                ? Decode(File.ReadAllBytes(HostsPath))
                : ([], new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (loadedFingerprint is not null
                && !string.Equals(Fingerprint(ParseEntries(current)), loadedFingerprint, StringComparison.Ordinal))
            {
                return HostsSaveOutcome.ChangedOutside;
            }

            // Preserve the pristine original: back up only if we have never backed up before.
            var keptACopy = false;
            if (exists && !File.Exists(BackupPath))
            {
                File.Copy(HostsPath, BackupPath, overwrite: false);
                keptACopy = true;
            }

            var lines = new List<string>
            {
                ManagedHeaderLine1,
                ManagedHeaderLine2,
                ""
            };

            // Re-emit the user's own standalone comments/blank lines (not IP mappings — those are
            // carried by `entries`). Without this, editing one entry through the UI silently deleted
            // every hand-written comment on the next save.
            var preserved = PreservedLines(current);
            if (preserved.Count > 0)
            {
                lines.AddRange(preserved);
                lines.Add("");
            }

            foreach (var entry in entries)
            {
                string commentSuffix = string.IsNullOrWhiteSpace(entry.Comment) ? "" : $" # {entry.Comment}";
                string line = $"{entry.IpAddress}\t{entry.Hostname}{commentSuffix}";
                if (!entry.IsEnabled)
                    line = "# " + line;
                lines.Add(line);
            }

            // Write atomically: a crash part-way through File.WriteAllLines would otherwise
            // leave the hosts file truncated or empty. Write to a temp file in the same
            // directory, then swap it into place in one operation.
            // Unique per call — see the note in RestoreBackup and AtomicFile.UniqueTempPath.
            var tempPath = AtomicFile.UniqueTempPath(HostsPath, "sysmanager.tmp");
            try
            {
                File.WriteAllLines(tempPath, lines, encoding);

                // AtomicFile.SwapIntoPlace owns the two-branch swap and the flush that has to precede
                // it, so the ACL rationale is stated once there instead of twice here: File.Replace copies
                // the replaced file's security descriptor onto the replacement, which is how the hardened
                // system hosts file keeps its DACL, and it needs the destination to already exist.
                AtomicFile.SwapIntoPlace(tempPath, HostsPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch (IOException) { /* best-effort temp cleanup */ }
                    catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
                }
            }
            return keptACopy ? HostsSaveOutcome.SavedAndKeptACopy : HostsSaveOutcome.Saved;
        }
    }

    /// <summary>
    /// Restores the hosts file from the pristine backup created before SysManager
    /// first modified it. Returns false if there is no backup to restore from.
    /// </summary>
    /// <remarks>
    /// Shares <see cref="_writeLock"/> with <see cref="SaveHosts"/> — the second mutator of the same
    /// file, so it belongs behind the same gate rather than a second one: a restore that interleaved
    /// with a save would let the save's swap land after the restore's and silently undo it. Same
    /// two-mutators-one-gate shape as <c>VolumePresetService.Save</c>/<c>.Delete</c>. The
    /// <c>File.Exists</c> check is inside, because "is there a backup" is only true until the other
    /// mutator runs.
    /// </remarks>
    public bool RestoreBackup()
    {
        lock (_writeLock)
        {
            if (!File.Exists(BackupPath)) return false;

            // Preserve the hardened hosts-file DACL the same way SaveHosts does: stage the
            // backup content in a temp file then File.Replace it in, which copies the
            // existing target's security descriptor onto the replacement. A plain
            // File.Copy(overwrite:true) would relink a new inode that inherits only the
            // directory's default ACL, silently weakening the file.
            // Unique per call: the cleanup below runs in a finally, so a fixed name would let two
            // overlapping writers delete each other's staged file and lose both. AtomicFile owns the
            // naming so the rule is stated once (see AtomicFile.UniqueTempPath).
            var tempPath = AtomicFile.UniqueTempPath(HostsPath, "sysmanager.restore.tmp");
            try
            {
                File.Copy(BackupPath, tempPath, overwrite: true);

                AtomicFile.SwapIntoPlace(tempPath, HostsPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch (IOException) { /* best-effort temp cleanup */ }
                    catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Validates and adds a new entry. Throws <see cref="ArgumentException"/> on invalid input.
    /// </summary>
    public HostsEntry AddEntry(string ip, string hostname)
    {
        if (!IPAddress.TryParse(ip, out _))
            throw new ArgumentException($"Invalid IP address: {ip}", nameof(ip));

        if (string.IsNullOrWhiteSpace(hostname))
            throw new ArgumentException("Hostname cannot be empty.", nameof(hostname));

        if (!HostnameRegex().IsMatch(hostname))
            throw new ArgumentException($"Invalid hostname: {hostname}. No spaces or special characters allowed.", nameof(hostname));

        return new HostsEntry
        {
            IpAddress = ip.Trim(),
            Hostname = hostname.Trim(),
            IsEnabled = true
        };
    }

    /// <summary>
    /// Determines whether <paramref name="candidate"/> is a hosts entry (IP + at least one hostname):
    /// strip an optional inline comment, split on whitespace, require at least two tokens, and
    /// validate the first as an IP address.
    /// </summary>
    /// <remarks>
    /// This is the single acceptance test for "would ReadHostsAsync turn this into a HostsEntry", and
    /// all three callers depend on that being one definition rather than three lookalikes:
    /// <see cref="ParseEntries"/>, which both reads go through, to decide whether a '#' line is a disabled entry;
    /// and <see cref="PreservedLines"/> twice, to avoid re-emitting a line that is already carried as
    /// an entry. If this drifted from what ReadHostsAsync accepts, lines would be silently duplicated
    /// or silently dropped depending on which way it drifted.
    /// <para>Named for what it computes rather than for one caller. It replaced a
    /// <c>LooksLikeIpStart</c> heuristic that only inspected the leading character, which missed IPv6
    /// addresses starting with hex letters (<c>fe80::</c>) and falsely matched digit-leading comments
    /// (<c># 5G adapter notes</c>).</para>
    /// </remarks>
    private static bool IsEntryLine(string candidate)
    {
        if (candidate.Length == 0) return false;

        // Mirror ReadHostsAsync: strip inline comment, then tokenize.
        string[] parts = candidate.Split(['#'], 2);
        string entryPart = parts[0].Trim();
        string[] tokens = entryPart.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        return tokens.Length >= 2 && IPAddress.TryParse(tokens[0], out _);
    }
}

/// <summary>Whether the hosts file differs from the copy kept beside it, whether SysManager has written it, and when that
/// copy was made.</summary>
/// <param name="DiffersFromHosts">True while putting the copy back would change the hosts file.</param>
/// <param name="WrittenBySysManager">True when the hosts file starts with the header SysManager writes into it.</param>
/// <param name="TakenAtUtc">When the copy was made.</param>
public sealed record HostsBackupState(bool DiffersFromHosts, bool WrittenBySysManager, DateTime TakenAtUtc);

/// <summary>What <see cref="HostsFileService.SaveHosts"/> did.</summary>
public enum HostsSaveOutcome
{
    /// <summary>Written.</summary>
    Saved,

    /// <summary>Written, after keeping the file as it was beside it as hosts.bak, there being no copy yet.</summary>
    SavedAndKeptACopy,

    /// <summary>Not written: the file no longer holds the entries the list was read from.</summary>
    ChangedOutside,
}
