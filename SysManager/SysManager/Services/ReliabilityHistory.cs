// SysManager · ReliabilityHistory — what Windows recorded behind Reliability Monitor
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>One record of the history behind Reliability Monitor (<c>Win32_ReliabilityRecords</c>).</summary>
/// <param name="When">When Windows recorded it, in local time.</param>
/// <param name="Source">The SourceName: <c>Microsoft-Windows-WindowsUpdateClient</c>, <c>MsiInstaller</c>, …</param>
/// <param name="EventId">The EventIdentifier.</param>
/// <param name="Product">The ProductName Windows filled in, or empty.</param>
/// <param name="Message">The record's message.</param>
public sealed record ReliabilityRecord(DateTime When, string Source, int EventId, string Product, string Message);

/// <summary>The records read, or that the history could not be read at all.</summary>
public sealed record ReliabilityRead(IReadOnlyList<ReliabilityRecord> Records, bool Readable)
{
    public static readonly ReliabilityRead Unreadable = new([], false);
}

/// <summary>Windows' own record of what was installed, updated and what failed (#1507).</summary>
/// <remarks>
/// The one source that knows what happened while SysManager was closed. Behind an interface so a test hands the
/// reading side fixed records and nothing queries the PC running the suite.
/// </remarks>
public interface IReliabilityHistory
{
    /// <summary>The records since <paramref name="since"/>. Runs off the UI thread.</summary>
    Task<ReliabilityRead> ReadAsync(DateTime since, CancellationToken ct = default);
}

/// <summary>Reads <c>Win32_ReliabilityRecords</c> through WMI, which needs no administrator rights.</summary>
public sealed class WmiReliabilityHistory : IReliabilityHistory
{
    /// <summary>How long one read may take. The history is local, so this bounds a WMI service that is not answering.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    public Task<ReliabilityRead> ReadAsync(DateTime since, CancellationToken ct = default) =>
        Task.Run(() => Read(since, ct), ct);

    private static ReliabilityRead Read(DateTime since, CancellationToken ct)
    {
        // Filtered by Windows rather than here: the history keeps a year, and only the period is wanted.
        var query = "SELECT TimeGenerated, SourceName, EventIdentifier, ProductName, Message FROM Win32_ReliabilityRecords "
                    + $"WHERE TimeGenerated >= '{ManagementDateTimeConverter.ToDmtfDateTime(since)}'";
        var records = new List<ReliabilityRecord>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery(query),
                new EnumerationOptions { Timeout = ReadTimeout, ReturnImmediately = true, Rewindable = false });
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    ct.ThrowIfCancellationRequested();
                    if (mo["TimeGenerated"] is not string generated) continue;
                    records.Add(new ReliabilityRecord(
                        ManagementDateTimeConverter.ToDateTime(generated),
                        mo["SourceName"]?.ToString() ?? "",
                        mo["EventIdentifier"] is { } id ? Convert.ToInt32(id, CultureInfo.InvariantCulture) : 0,
                        mo["ProductName"]?.ToString()?.Trim() ?? "",
                        mo["Message"]?.ToString() ?? ""));
                }
            }
            return new ReliabilityRead(records, Readable: true);
        }
        catch (ManagementException ex) { Log.Debug("Reliability history unavailable: {Error}", ex.Message); }
        catch (COMException ex) { Log.Debug("Reliability history WMI COM error: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Reliability history access denied: {Error}", ex.Message); }
        catch (ArgumentOutOfRangeException ex) { Log.Debug("Reliability history held a time it could not read: {Error}", ex.Message); }
        return ReliabilityRead.Unreadable;
    }
}

/// <summary>Turns reliability records into changes and problem counts, without touching the PC.</summary>
public static partial class ReliabilityChanges
{
    private const string WindowsUpdateSource = "Microsoft-Windows-WindowsUpdateClient";
    private const string InstallerSource = "MsiInstaller";

    /// <summary>
    /// The changes the records describe, newest first, and the programs that crashed or stopped responding.
    /// </summary>
    /// <remarks>
    /// Windows can write the same record several times over, to the second, and Windows Installer writes two records
    /// for one install; each is listed once. A Windows Installer record with a non-zero status is a failed install, not
    /// a change, and is left out. Other kinds of record are not changes and are not listed.
    /// </remarks>
    public static (IReadOnlyList<ChangeEvent> Changes, IReadOnlyList<ProblemEvent> Problems) Parse(IEnumerable<ReliabilityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var changes = new List<ChangeEvent>();
        var problems = new List<ProblemEvent>();
        foreach (var record in records.DistinctBy(r => (r.When, r.Source, r.EventId, r.Product, r.Message)))
        {
            switch (record.Source, record.EventId)
            {
                case ("Application Error", 1000): problems.Add(new ProblemEvent(record.When, StoppedResponding: false)); break;
                case ("Application Hang", 1002): problems.Add(new ProblemEvent(record.When, StoppedResponding: true)); break;
                case (WindowsUpdateSource, 19): changes.Add(Installed(record)); break;
                case (WindowsUpdateSource, 20): changes.Add(Failed(record)); break;
                case (InstallerSource, 1033 or 11707):
                    if (Succeeded(record) && ProductOf(record) is { } installed)
                        changes.Add(new ChangeEvent(record.When, null, ChangeKind.ProgramInstalled, installed,
                            "An installer", "Recorded by Windows Installer"));
                    break;
                case (InstallerSource, 1034 or 11724):
                    if (Succeeded(record) && ProductOf(record) is { } removed)
                        changes.Add(new ChangeEvent(record.When, null, ChangeKind.ProgramRemoved, removed,
                            "An uninstaller", "Recorded by Windows Installer"));
                    break;
            }
        }

        // One change recorded twice lands within a moment of itself: Windows Installer's pair for one install (1033 and
        // 11707), or an update Windows wrote again a second later.
        var listed = new List<ChangeEvent>();
        foreach (var change in changes.OrderByDescending(c => c.When))
        {
            if (listed.Any(c => c.Kind == change.Kind
                                && string.Equals(c.Subject, change.Subject, StringComparison.OrdinalIgnoreCase)
                                && (c.When - change.When).Duration() <= TimeSpan.FromMinutes(2)))
                continue;
            listed.Add(change);
        }
        return (listed, problems);
    }

    private static ChangeEvent Installed(ReliabilityRecord record)
    {
        var title = UpdateTitle(record);
        if (StoreName(title) is { } app)
            return new ChangeEvent(record.When, null, ChangeKind.StoreAppUpdate, app, "Microsoft Store", "");
        if (title.Contains("Security Intelligence Update", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Definition Update", StringComparison.OrdinalIgnoreCase))
            return new ChangeEvent(record.When, null, ChangeKind.DefenderUpdate, title, "Windows Update", "");
        if (DriverPattern().Match(title) is { Success: true } driver)
            return new ChangeEvent(record.When, null, ChangeKind.DriverUpdate,
                $"{driver.Groups["vendor"].Value.Trim()} {driver.Groups["device"].Value.Trim()}", "Windows Update",
                $"Version {driver.Groups["version"].Value}");
        return new ChangeEvent(record.When, null, ChangeKind.WindowsUpdate, title, "Windows Update", "");
    }

    private static ChangeEvent Failed(ReliabilityRecord record)
    {
        var title = UpdateTitle(record);
        var store = StoreName(title);
        return new ChangeEvent(record.When, null, ChangeKind.UpdateFailed, store ?? title,
            store is null ? "Windows Update" : "Microsoft Store", "Windows will try again on its own");
    }

    /// <summary>The update's name: the product Windows recorded, or the name its message ends with.</summary>
    private static string UpdateTitle(ReliabilityRecord record)
    {
        if (record.Product.Length > 0) return record.Product;
        var colon = record.Message.LastIndexOf(": ", StringComparison.Ordinal);
        return colon >= 0 ? record.Message[(colon + 2)..].Trim().TrimEnd('.') : record.Message.Trim();
    }

    /// <summary>A Store app's name: what follows the twelve-character Store id Windows puts in front of it.</summary>
    private static string? StoreName(string title) =>
        StorePattern().Match(title) is { Success: true } m ? m.Groups["app"].Value : null;

    private static bool Succeeded(ReliabilityRecord record) =>
        StatusPattern().Match(record.Message) is not { Success: true } status || status.Groups["code"].Value == "0";

    private static string? ProductOf(ReliabilityRecord record)
    {
        if (record.Product.Length > 0) return record.Product;
        var m = ProductNamePattern().Match(record.Message);
        return m.Success ? m.Groups["name"].Value.Trim() : null;
    }

    [GeneratedRegex(@"^[0-9A-Z]{12}-(?<app>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex StorePattern();

    /// <summary>A driver as Windows Update names one: "Vendor - Device - 1.2.3.4".</summary>
    [GeneratedRegex(@"^(?<vendor>[^-]+?) - (?<device>.+?) - (?<version>\d+(?:\.\d+)+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DriverPattern();

    [GeneratedRegex(@"(?:success or error status|Error status): (?<code>-?\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StatusPattern();

    [GeneratedRegex(@"(?:Product Name: (?<name>[^\r\n]+?)\. Product Version|Product: (?<name>[^\r\n]+?) --)", RegexOptions.CultureInvariant)]
    private static partial Regex ProductNamePattern();
}
