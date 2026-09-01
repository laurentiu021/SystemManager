// SysManager · HostsFileService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Net;
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
        List<HostsEntry> entries = [];
        if (!File.Exists(HostsPath)) return entries;

        var lines = await File.ReadAllLinesAsync(HostsPath, ct).ConfigureAwait(false);
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
    /// </remarks>
    private List<string> ReadPreservedLines()
    {
        List<string> preserved = [];
        if (!File.Exists(HostsPath)) return preserved;

        string[] rawLines;
        try { rawLines = File.ReadAllLines(HostsPath); }
        catch (IOException) { return preserved; }
        catch (UnauthorizedAccessException) { return preserved; }

        // Trim in the projection: the loop body never needs the untrimmed line, and rawLines is
        // already a materialised array from ReadAllLines, so this is the same work in the same order.
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

    /// <summary>True if a pristine pre-SysManager backup of the hosts file exists.</summary>
    public bool HasBackup => File.Exists(BackupPath);

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
    /// accumulate duplicate headers or blanks. See <see cref="ReadPreservedLines"/>.
    /// </remarks>
    public void SaveHosts(List<HostsEntry> entries)
    {
        // Preserve the pristine original: back up only if we have never backed up before.
        if (File.Exists(HostsPath) && !File.Exists(BackupPath))
            File.Copy(HostsPath, BackupPath, overwrite: false);

        var lines = new List<string>
        {
            ManagedHeaderLine1,
            ManagedHeaderLine2,
            ""
        };

        // Re-emit the user's own standalone comments/blank lines (not IP mappings — those are
        // carried by `entries`). Without this, editing one entry through the UI silently deleted
        // every hand-written comment on the next save.
        var preserved = ReadPreservedLines();
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

        // Write atomically: a crash midway through File.WriteAllLines would otherwise
        // leave the hosts file truncated or empty. Write to a temp file in the same
        // directory, then swap it into place in one operation.
        // Unique per call — see the note in RestoreBackup and AtomicFile.UniqueTempPath.
        var tempPath = AtomicFile.UniqueTempPath(HostsPath, "sysmanager.tmp");
        try
        {
            File.WriteAllLines(tempPath, lines);

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
    }

    /// <summary>
    /// Restores the hosts file from the pristine backup created before SysManager
    /// first modified it. Returns false if there is no backup to restore from.
    /// </summary>
    public bool RestoreBackup()
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
    /// ReadHostsAsync itself, to decide whether a '#' line is a disabled entry; and
    /// <see cref="ReadPreservedLines"/> twice, to avoid re-emitting a line that is already carried as
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
