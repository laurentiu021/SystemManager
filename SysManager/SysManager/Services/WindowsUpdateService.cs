// SysManager · WindowsUpdateService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Direct wrapper over the Windows Update Agent (WUA) COM API
/// (Microsoft.Update.Session). Replaces PSWindowsUpdate for scan and install
/// because PSWindowsUpdate's Install-WindowsUpdate filters out optional driver
/// updates client-side, even when the COM API can install them just fine.
///
/// All operations are blocking COM calls — callers must dispatch them via
/// Task.Run so they don't block the UI thread.
/// </summary>
public sealed class WindowsUpdateService
{
    /// <summary>Raised on the calling thread for each progress/log line.</summary>
    public event Action<string>? Log;

    private void Emit(string text) => Log?.Invoke(text);

    /// <summary>
    /// Scan for available updates (IsInstalled=0). Returns one entry per
    /// update with stable UpdateID for later install.
    /// </summary>
    public Task<IReadOnlyList<UpdateEntry>> ScanAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<UpdateEntry>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            Emit("Connecting to Windows Update…");

            // Declared outside the try so the finally can release every COM object
            // even when cancellation or MapToEntry throws mid-scan (previously these
            // releases sat on the happy path only, leaking COM objects on any throw).
            dynamic? session = null, searcher = null, result = null, updates = null;
            try
            {
                session = CreateSession();
                searcher = session.CreateUpdateSearcher();
                searcher.IncludePotentiallySupersededUpdates = false;

                // "IsInstalled=0" returns everything not yet installed,
                // including optional drivers and feature upgrades.
                result = searcher.Search("IsInstalled=0");
                ct.ThrowIfCancellationRequested();

                updates = result.Updates;
                var count = (int)updates.Count;
                Emit($"Found {count} update(s).");

                var list = new List<UpdateEntry>(count);
                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var u = updates.Item(i);
                    list.Add(MapToEntry(u));
                    Marshal.FinalReleaseComObject(u);
                }
                return list;
            }
            finally
            {
                if (updates is not null) Marshal.FinalReleaseComObject(updates);
                if (result is not null) Marshal.FinalReleaseComObject(result);
                if (searcher is not null) Marshal.FinalReleaseComObject(searcher);
                if (session is not null) Marshal.FinalReleaseComObject(session);
            }
        }, ct);

    /// <summary>
    /// Download + install the given updates. Per-update progress is reported
    /// via <see cref="Log"/>; per-update result is set on the entry's
    /// <see cref="UpdateEntry.Status"/> property.
    /// Returns aggregate counts (installed, failed, rebootRequired).
    /// </summary>
    public Task<InstallReport> InstallAsync(
        IReadOnlyList<UpdateEntry> entries,
        CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (entries is null || entries.Count == 0)
                return new InstallReport(0, 0, false);

            ct.ThrowIfCancellationRequested();
            Emit("Connecting to Windows Update…");
            var session = CreateSession();
            var searcher = session.CreateUpdateSearcher();
            var search = searcher.Search("IsInstalled=0");
            var liveUpdates = search.Updates;
            ct.ThrowIfCancellationRequested();

            // Build a UpdateID -> live COM object map for the requested entries.
            var byId = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            var liveCount = (int)liveUpdates.Count;
            for (int i = 0; i < liveCount; i++)
            {
                var u = liveUpdates.Item(i);
                var id = (string)u.Identity.UpdateID;
                if (!byId.ContainsKey(id))
                    byId[id] = u;
                else
                    Marshal.FinalReleaseComObject(u);
            }

            int installed = 0;
            int failed = 0;
            bool reboot = false;

            try
            {
                int idx = 0;
                foreach (var entry in entries)
                {
                    idx++;
                    ct.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(entry.UpdateId) || !byId.TryGetValue(entry.UpdateId, out var u))
                    {
                        Emit($"[{idx}/{entries.Count}] SKIP (not in live feed): {entry.Title}");
                        entry.Status = "Not applied";
                        continue;
                    }

                    Emit($"[{idx}/{entries.Count}] {entry.Title}");
                    entry.Status = "Downloading…";

                    var coll = CreateUpdateCollection();
                    coll.Add(u);

                    try
                    {
                        if (!(bool)u.IsDownloaded)
                        {
                            Emit("  Downloading…");
                            var dl = session.CreateUpdateDownloader();
                            dl.Updates = coll;
                            var dlResult = dl.Download();
                            var dlCode = (int)dlResult.ResultCode;
                            Marshal.FinalReleaseComObject(dlResult);
                            Marshal.FinalReleaseComObject(dl);

                            if (dlCode != 2)
                            {
                                Emit($"  Download failed (code {dlCode}).");
                                entry.Status = "Failed (download)";
                                failed++;
                                Marshal.FinalReleaseComObject(coll);
                                continue;
                            }
                        }

                        if (!(bool)u.EulaAccepted)
                        {
                            try { u.AcceptEula(); }
                            catch (COMException ex) { Log?.Invoke($"  EULA accept failed: 0x{ex.HResult:X8}"); }
                        }

                        ct.ThrowIfCancellationRequested();
                        Emit("  Installing…");
                        entry.Status = "Installing…";

                        var inst = session.CreateUpdateInstaller();
                        inst.Updates = coll;
                        var iResult = inst.Install();
                        var iCode = (int)iResult.ResultCode;
                        bool needsReboot = (bool)iResult.RebootRequired;
                        Marshal.FinalReleaseComObject(iResult);
                        Marshal.FinalReleaseComObject(inst);

                        if (iCode == 2)
                        {
                            entry.Status = needsReboot ? "Installed (reboot required)" : "Installed";
                            installed++;
                            if (needsReboot) reboot = true;
                            Emit("  ✓ Installed");
                        }
                        else
                        {
                            entry.Status = $"Failed (install code {iCode})";
                            failed++;
                            Emit($"  ✗ Install failed (code {iCode}).");
                        }
                    }
                    catch (COMException ex)
                    {
                        entry.Status = $"Error 0x{ex.HResult:X8}";
                        failed++;
                        Emit($"  ✗ COM error: 0x{ex.HResult:X8} {ex.Message}");
                        Serilog.Log.Warning(ex, "Install failed for {Title}", entry.Title);
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(coll);
                    }
                }
            }
            finally
            {
                foreach (var u in byId.Values) Marshal.FinalReleaseComObject(u);
                Marshal.FinalReleaseComObject(liveUpdates);
                Marshal.FinalReleaseComObject(search);
                Marshal.FinalReleaseComObject(searcher);
                Marshal.FinalReleaseComObject(session);
            }

            return new InstallReport(installed, failed, reboot);
        }, ct);

    private static dynamic CreateSession()
    {
        var t = Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)!;
        return Activator.CreateInstance(t)!;
    }

    private static dynamic CreateUpdateCollection()
    {
        var t = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", throwOnError: true)!;
        return Activator.CreateInstance(t)!;
    }

    /// <summary>
    /// Convert a live IUpdate COM object to a snapshot UpdateEntry.
    /// Internal so unit tests can exercise category classification.
    /// </summary>
    internal static UpdateEntry MapToEntry(dynamic u)
    {
        var title = (string)(u.Title ?? string.Empty);
        var kbList = ExtractKbIds(u);
        var kb = kbList.Count > 0 ? "KB" + string.Join(",", kbList) : string.Empty;
        long size = 0;
        try { size = (long)(decimal)u.MaxDownloadSize; }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) { Serilog.Log.Debug(ex, "Windows Update: MaxDownloadSize not exposed for {Title}", title); }
        catch (COMException ex) { Serilog.Log.Debug(ex, "Windows Update: COM error reading size for {Title}", title); }
        return new UpdateEntry
        {
            Title = title,
            KB = kb,
            Size = FormatSize(size),
            UpdateId = (string)u.Identity.UpdateID,
            IsHidden = (bool)u.IsHidden,
            Category = ClassifyCategory(title, u),
            Status = "Available",
        };
    }

    private static List<string> ExtractKbIds(dynamic u)
    {
        List<string> list = [];
        try
        {
            var ids = u.KBArticleIDs;
            int n = (int)ids.Count;
            for (int i = 0; i < n; i++)
            {
                var id = (string)ids.Item(i);
                if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
            }
        }
        catch (COMException ex) { Serilog.Log.Debug(ex, "ExtractKbIds: COM error"); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) { Serilog.Log.Debug(ex, "ExtractKbIds: dynamic binding error"); }
        return list;
    }

    /// <summary>
    /// Classify an update into a SysManager category for the colored pill in
    /// the DataGrid. Order matters — most specific first.
    /// </summary>
    internal static string ClassifyCategory(string title, dynamic? u = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Update";

        if (title.Contains("Defender", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Definition Update", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Antimalware", StringComparison.OrdinalIgnoreCase))
            return "Defender";

        // COM Categories collection — check Type when available.
        if (u is not null)
        {
            try
            {
                var cats = u.Categories;
                int n = (int)cats.Count;
                for (int i = 0; i < n; i++)
                {
                    var cat = cats.Item(i);
                    var name = (string)(cat.Name ?? string.Empty);
                    Marshal.FinalReleaseComObject(cat);
                    if (name.Equals("Drivers", StringComparison.OrdinalIgnoreCase)) return "Driver";
                    if (name.Equals("Upgrades", StringComparison.OrdinalIgnoreCase)) return "Feature upgrade";
                    if (name.Contains("Servicing", StringComparison.OrdinalIgnoreCase)) return "Servicing";
                }
                Marshal.FinalReleaseComObject(cats);
            }
            catch (COMException ex) { Serilog.Log.Debug(ex, "ClassifyCategory: COM error reading Categories"); }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) { Serilog.Log.Debug(ex, "ClassifyCategory: dynamic binding error"); }
        }

        if (title.Contains("Driver", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Firmware", StringComparison.OrdinalIgnoreCase))
            return "Driver";
        if (title.Contains("Cumulative Update", StringComparison.OrdinalIgnoreCase)) return "Cumulative";
        if (title.Contains("Security Update", StringComparison.OrdinalIgnoreCase)) return "Security";
        if (title.Contains("Servicing Stack", StringComparison.OrdinalIgnoreCase)) return "Servicing";
        if (title.Contains(".NET", StringComparison.OrdinalIgnoreCase)) return ".NET";
        return "Update";
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        > 0 => $"{bytes} B",
        _ => "",
    };
}

/// <summary>Aggregate result of an install batch.</summary>
public readonly record struct InstallReport(int Installed, int Failed, bool RebootRequired);
