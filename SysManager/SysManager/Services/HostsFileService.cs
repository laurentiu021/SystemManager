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
    private static readonly string HostsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "drivers", "etc", "hosts");

    private static readonly string BackupPath = HostsPath + ".bak";

    [GeneratedRegex(@"^[a-zA-Z0-9]([a-zA-Z0-9\-\.]*[a-zA-Z0-9])?$")]
    private static partial Regex HostnameRegex();

    /// <summary>
    /// Reads and parses the hosts file. Skips pure comment lines (starting with #
    /// that don't have an IP pattern). Detects commented-out entries as disabled.
    /// </summary>
    public List<HostsEntry> ReadHosts()
    {
        var entries = new List<HostsEntry>();
        if (!File.Exists(HostsPath)) return entries;

        foreach (string rawLine in File.ReadAllLines(HostsPath))
        {
            string line = rawLine.Trim();
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
            string hostname = tokens[1];

            // Validate IP
            if (!IPAddress.TryParse(ip, out _)) continue;

            entries.Add(new HostsEntry
            {
                IpAddress = ip,
                Hostname = hostname,
                Comment = comment,
                IsEnabled = !isDisabled
            });
        }

        return entries;
    }

    /// <summary>
    /// Saves entries back to the hosts file. Disabled entries are written as commented lines.
    /// Creates a backup before writing.
    /// </summary>
    public void SaveHosts(List<HostsEntry> entries)
    {
        // Backup existing file
        if (File.Exists(HostsPath))
            File.Copy(HostsPath, BackupPath, overwrite: true);

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

        File.WriteAllLines(HostsPath, lines);
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
