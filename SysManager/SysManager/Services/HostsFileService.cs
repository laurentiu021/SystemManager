// SysManager · HostsFileService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Net;
using System.Text.RegularExpressions;
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
    /// Creates the service against the real system hosts file. The optional
    /// <paramref name="hostsPath"/> override exists for testing so the backup /
    /// restore logic can be exercised without touching System32 or needing admin.
    /// </summary>
    public HostsFileService(string? hostsPath = null)
    {
        HostsPath = hostsPath ?? DefaultHostsPath;
        BackupPath = HostsPath + ".bak";
    }

    [GeneratedRegex(@"^[a-zA-Z0-9]([a-zA-Z0-9\-\.]*[a-zA-Z0-9])?$")]
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
                // If the remainder doesn't start with something that looks like an IP, skip it
                if (!LooksLikeIpStart(workLine)) continue;
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
    /// </remarks>
    public void SaveHosts(List<HostsEntry> entries)
    {
        // Preserve the pristine original: back up only if we have never backed up before.
        if (File.Exists(HostsPath) && !File.Exists(BackupPath))
            File.Copy(HostsPath, BackupPath, overwrite: false);

        var lines = new List<string>
        {
            "# This file is managed by SysManager",
            "# Entries marked with # at the start are disabled",
            ""
        };

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
        // directory, then replace the target in one move.
        var tempPath = HostsPath + ".sysmanager.tmp";
        try
        {
            File.WriteAllLines(tempPath, lines);
            File.Move(tempPath, HostsPath, overwrite: true);
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
        File.Copy(BackupPath, HostsPath, overwrite: true);
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
    /// Quick check: does the string start with a digit or colon (IPv6)?
    /// </summary>
    private static bool LooksLikeIpStart(string s) =>
        s.Length > 0 && (char.IsDigit(s[0]) || s[0] == ':');
}
