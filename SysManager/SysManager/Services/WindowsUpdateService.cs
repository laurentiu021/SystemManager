// SysManager · WindowsUpdateService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
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
public sealed class WindowsUpdateService : IWindowsUpdateService
{
    // Typed dynamic, as WUA's objects are read: a factory typed object would tell code scanning that what it makes is
    // a bare object, which has none of the members the late-bound calls on it name.
    private readonly Func<dynamic> _createSession;
    private readonly Func<dynamic> _createUpdateCollection;

    public WindowsUpdateService() : this(CreateSession, CreateUpdateCollection)
    {
    }

    /// <summary>
    /// The agent over <paramref name="createSession"/> and <paramref name="createUpdateCollection"/> in place of WUA's
    /// <c>Microsoft.Update.Session</c> and <c>Microsoft.Update.UpdateColl</c>.
    /// </summary>
    /// <remarks>
    /// So a test can run a scan and an install over objects of its own, which a real session would replace with a
    /// search of Microsoft's servers and a real install. Every object is read late-bound, as WUA's are, and released
    /// through <see cref="ReleaseComObject"/>, which leaves an object that is not a COM one alone.
    /// </remarks>
    internal WindowsUpdateService(Func<dynamic> createSession, Func<dynamic> createUpdateCollection)
    {
        _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        _createUpdateCollection =
            createUpdateCollection ?? throw new ArgumentNullException(nameof(createUpdateCollection));
    }

    /// <summary>What the scan and the install ask Windows Update for: updates not installed and not hidden.</summary>
    /// <remarks>
    /// A criteria string replaces the search's default, which is these same two conditions, rather than adding to it.
    /// Both searches passed <c>IsInstalled=0</c> alone, so an update the user had hidden with "Show or hide updates",
    /// usually a driver that breaks the PC, was listed, ticked and installed, and the Dashboard counted it as pending
    /// (#2727). It still includes optional drivers and feature upgrades.
    /// </remarks>
    internal const string PendingCriteria = "IsInstalled=0 and IsHidden=0";

    /// <summary>
    /// Scan for the updates Windows has pending, leaving out the ones the user hid (<see cref="PendingCriteria"/>).
    /// Returns one entry per update with stable UpdateID for later install.
    /// </summary>
    public Task<IReadOnlyList<UpdateEntry>> ScanAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<UpdateEntry>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report("Connecting to Windows Update…");

            // Declared outside the try so the finally can release every COM object
            // even when cancellation or MapToEntry throws mid-scan (previously these
            // releases sat on the happy path only, leaking COM objects on any throw).
            dynamic? session = null, searcher = null, result = null, updates = null;
            try
            {
                session = _createSession();
                searcher = session.CreateUpdateSearcher();
                searcher.IncludePotentiallySupersededUpdates = false;

                result = searcher.Search(PendingCriteria);
                ct.ThrowIfCancellationRequested();

                updates = result.Updates;
                var count = (int)updates.Count;
                progress?.Report($"Found {count} update(s).");

                var list = new List<UpdateEntry>(count);
                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var u = updates.Item(i);
                    list.Add(MapToEntry(u));
                    ReleaseComObject(u);
                }
                return list;
            }
            finally
            {
                ReleaseComObject(updates);
                ReleaseComObject(result);
                ReleaseComObject(searcher);
                ReleaseComObject(session);
            }
        }, ct);

    /// <summary>
    /// Download + install the given updates. Per-update progress is reported
    /// to <paramref name="progress"/>; per-update result is set on the entry's
    /// <see cref="UpdateEntry.Status"/> property.
    /// Returns aggregate counts (installed, failed, rebootRequired), and why it stopped early if it did.
    /// </summary>
    /// <remarks>
    /// A report on every exit once it has started. A stop on <paramref name="ct"/>, before an update or between its
    /// download and its install, and a Windows Update Agent error, in the search the install starts with or between
    /// updates, used to end the run as an exception: the updates already installed went uncounted, the restart they
    /// needed was never mentioned, and every row it had not finished kept saying "Pending…" (#2791).
    /// </remarks>
    public Task<InstallReport> InstallAsync(
        IReadOnlyList<UpdateEntry> entries,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (entries is null || entries.Count == 0)
                return new InstallReport(0, 0, false);

            if (ct.IsCancellationRequested) return new InstallReport(0, 0, false, Cancelled: true);
            progress?.Report("Connecting to Windows Update…");

            // Declared outside the try so the finally releases every COM object even
            // when a cancellation check or the map-build throws during setup (these
            // previously sat on the happy path and leaked on any throw).
            dynamic? session = null, searcher = null, search = null, liveUpdates = null;
            var byId = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);

            int installed = 0;
            int failed = 0;
            bool reboot = false;
            bool cancelled = false;
            int agentError = 0;

            try
            {
                session = _createSession();
                searcher = session.CreateUpdateSearcher();
                search = searcher.Search(PendingCriteria);
                liveUpdates = search.Updates;

                // Build a UpdateID -> live COM object map for the requested entries.
                var liveCount = (int)liveUpdates.Count;
                for (int i = 0; i < liveCount; i++)
                {
                    var u = liveUpdates.Item(i);
                    dynamic? idObj = null;
                    string id;
                    try { idObj = u.Identity; id = (string)idObj.UpdateID; }
                    finally { ReleaseComObject(idObj); }
                    if (!byId.ContainsKey(id))
                        byId[id] = u;
                    else
                        ReleaseComObject(u);
                }

                int idx = 0;
                foreach (var entry in entries)
                {
                    idx++;
                    if (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(entry.UpdateId) || !byId.TryGetValue(entry.UpdateId, out var u))
                    {
                        progress?.Report($"[{idx}/{entries.Count}] SKIP (not in live feed): {entry.Title}");
                        entry.Status = "Not applied";
                        continue;
                    }

                    progress?.Report($"[{idx}/{entries.Count}] {entry.Title}");
                    entry.Status = "Downloading…";

                    dynamic coll = _createUpdateCollection();
                    coll.Add(u);

                    try
                    {
                        if (!(bool)u.IsDownloaded)
                        {
                            progress?.Report("  Downloading…");
                            var dl = session.CreateUpdateDownloader();
                            int dlCode;
                            int dlHr;
                            try
                            {
                                dl.Updates = coll;
                                var dlResult = dl.Download();
                                try
                                {
                                    dlCode = EffectiveResultCode((int)dlResult.ResultCode, () => UpdateResultCode(dlResult));
                                    dlHr = ResultHResult(dlResult);
                                }
                                finally
                                {
                                    ReleaseComObject(dlResult);
                                }
                            }
                            finally
                            {
                                // Release the downloader even if Download() throws,
                                // otherwise a failed download leaks the COM object.
                                ReleaseComObject(dl);
                            }

                            if (dlCode != Succeeded)
                            {
                                entry.Status = FailureStatus("download", dlCode, dlHr);
                                progress?.Report($"  Download failed: {entry.Status}.");
                                Serilog.Log.Warning("Windows Update download failed for {Title}: result code {Code}, 0x{HResult:X8}",
                                    entry.Title, dlCode, dlHr);
                                failed++;
                                // Do NOT release `coll` here: `continue` still runs the outer
                                // finally, which releases it. Releasing it twice separates the
                                // RCW and the finally's second call throws InvalidComObjectException,
                                // aborting the whole install run on the first failed download.
                                continue;
                            }
                        }

                        if (!(bool)u.EulaAccepted)
                        {
                            try { u.AcceptEula(); }
                            catch (COMException ex)
                            {
                                progress?.Report($"  EULA accept failed: 0x{ex.HResult:X8}");
                                Serilog.Log.Warning(ex, "Windows Update licence acceptance failed for {Title}", entry.Title);
                            }
                        }

                        // Stopped between its download and its install, the update stays not installed.
                        if (ct.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }

                        progress?.Report("  Installing…");
                        entry.Status = "Installing…";

                        int iCode;
                        int iHr;
                        bool needsReboot;
                        var inst = session.CreateUpdateInstaller();
                        try
                        {
                            inst.Updates = coll;
                            var iResult = inst.Install();
                            try
                            {
                                iCode = EffectiveResultCode((int)iResult.ResultCode, () => UpdateResultCode(iResult));
                                iHr = ResultHResult(iResult);
                                needsReboot = (bool)iResult.RebootRequired;
                            }
                            finally
                            {
                                ReleaseComObject(iResult);
                            }
                        }
                        finally
                        {
                            // Release the installer even if Install() (or reading its
                            // result) throws — a failed install otherwise leaks it.
                            ReleaseComObject(inst);
                        }

                        if (iCode == Succeeded)
                        {
                            entry.Status = needsReboot ? "Installed (reboot required)" : "Installed";
                            installed++;
                            if (needsReboot) reboot = true;
                            progress?.Report("  ✓ Installed");
                        }
                        else
                        {
                            entry.Status = FailureStatus("install", iCode, iHr);
                            failed++;
                            progress?.Report($"  ✗ Install failed: {entry.Status}.");
                            Serilog.Log.Warning("Windows Update install failed for {Title}: result code {Code}, 0x{HResult:X8}",
                                entry.Title, iCode, iHr);
                        }
                    }
                    catch (COMException ex)
                    {
                        entry.Status = $"Error 0x{ex.HResult:X8}";
                        failed++;
                        progress?.Report($"  ✗ COM error: 0x{ex.HResult:X8} {ex.Message}");
                        Serilog.Log.Warning(ex, "Install failed for {Title}", entry.Title);
                    }
                    finally
                    {
                        ReleaseComObject(coll);
                    }
                }

                if (cancelled) progress?.Report("Cancelled.");
            }
            // The search the install starts with failed, or something between two updates did. What went in stands.
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                agentError = ex.HResult;
                progress?.Report($"✗ Windows Update Agent error: 0x{ex.HResult:X8} {ex.Message}");
                Serilog.Log.Warning(ex, "Windows Update install stopped after {Installed} installed", installed);
            }
            finally
            {
                foreach (var u in byId.Values) ReleaseComObject(u);
                ReleaseComObject(liveUpdates);
                ReleaseComObject(search);
                ReleaseComObject(searcher);
                ReleaseComObject(session);
            }

            return new InstallReport(installed, failed, reboot, cancelled, agentError);
        }, ct);

    /// <summary>WUA's <c>OperationResultCode.orcSucceeded</c>.</summary>
    private const int Succeeded = 2;

    /// <summary>WUA's <c>OperationResultCode.orcSucceededWithErrors</c>.</summary>
    private const int SucceededWithErrors = 3;

    /// <summary>
    /// The code that decides a one-update download or install. WUA's overall code, unless it is "succeeded with errors",
    /// which says only that some update had a problem: with one update in the collection, that update's own code
    /// decides whether it went in.
    /// </summary>
    internal static int EffectiveResultCode(int overall, Func<int> forTheUpdate) =>
        overall == SucceededWithErrors ? forTheUpdate() : overall;

    /// <summary>
    /// A failed row's status: the HRESULT Windows reported when it gave one, the code a search for the error needs
    /// (0x800F0922, 0x80073712), and the WUA result code only when there is none. The row used to show the result code
    /// alone, which is 4 for nearly every failure.
    /// </summary>
    internal static string FailureStatus(string phase, int resultCode, int hResult) =>
        hResult != 0 ? $"Failed ({phase} 0x{hResult:X8})" : $"Failed ({phase} code {resultCode})";

    /// <summary>The HRESULT of a WUA download or installation result: the overall one, or else the one update's.</summary>
    private static int ResultHResult(dynamic result)
    {
        var overall = (int)result.HResult;
        if (overall != 0) return overall;
        dynamic? one = null;
        try
        {
            one = result.GetUpdateResult(0);
            return (int)one.HResult;
        }
        finally
        {
            ReleaseComObject(one);
        }
    }

    /// <summary>The result code of the one update in a WUA download or installation result.</summary>
    private static int UpdateResultCode(dynamic result)
    {
        dynamic? one = null;
        try
        {
            one = result.GetUpdateResult(0);
            return (int)one.ResultCode;
        }
        finally
        {
            ReleaseComObject(one);
        }
    }

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

        // u.Identity returns a fresh IUpdateIdentity RCW each access — capture once and
        // release it, otherwise every scanned update leaks an Identity COM object.
        string updateId;
        dynamic? identity = null;
        try
        {
            identity = u.Identity;
            updateId = (string)identity.UpdateID;
        }
        finally
        {
            ReleaseComObject(identity);
        }

        return new UpdateEntry
        {
            Title = title,
            KB = kb,
            SizeDisplay = FormatSize(size),
            SizeBytes = size,
            UpdateId = updateId,
            IsHidden = (bool)u.IsHidden,
            Category = ClassifyCategory(title, u),
            Status = "Available",
        };
    }

    private static List<string> ExtractKbIds(dynamic u)
    {
        List<string> list = [];
        // u.KBArticleIDs returns a fresh COM string collection (RCW) each access;
        // release it in a finally so every scanned update doesn't leak one.
        dynamic? ids = null;
        try
        {
            ids = u.KBArticleIDs;
            int n = (int)ids.Count;
            for (int i = 0; i < n; i++)
            {
                var id = (string)ids.Item(i);
                if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
            }
        }
        catch (COMException ex) { Serilog.Log.Debug(ex, "ExtractKbIds: COM error"); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) { Serilog.Log.Debug(ex, "ExtractKbIds: dynamic binding error"); }
        finally
        {
            ReleaseComObject(ids);
        }
        return list;
    }

    /// <summary>Windows Update's classification of drivers.</summary>
    /// <remarks>Read off a PC's own update store, where WUA names it "Drivers" and gives its updates type 2.</remarks>
    internal const string DriversClassification = "EBFC1FC5-71A4-4F7B-9ACA-3B9A503104A0";

    /// <summary>Windows Update's classification of feature upgrades, a new version of Windows.</summary>
    internal const string UpgradesClassification = "3689BDC8-B205-4AF4-8D4A-A63924C5E9D5";

    /// <summary>Windows Update's classification of definition updates, which on Windows are Defender's.</summary>
    internal const string DefinitionUpdatesClassification = "E0789628-CE08-4437-BE74-2495B842F43B";

    /// <summary>WUA's <c>UpdateType.utDriver</c>.</summary>
    private const int DriverUpdateType = 2;

    /// <summary>
    /// Classify an update into a SysManager category for the colored pill in
    /// the DataGrid. Order matters — most specific first.
    /// </summary>
    /// <remarks>
    /// What Windows Update says the update is comes first, by its classification's ID and the update's type: a
    /// category's name and the title are in the display language. Matched on the English names "Drivers" and
    /// "Upgrades" and on English title words, nearly every row on German, French or Romanian Windows got the plain
    /// "Update" pill, so an optional driver or a feature upgrade, ticked like every other row, looked like any other
    /// update (#2791). The names and the title words stay behind it, for what has no ID to go by.
    /// </remarks>
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
            dynamic? cats = null;
            try
            {
                cats = u.Categories;
                int n = (int)cats.Count;
                for (int i = 0; i < n; i++)
                {
                    var cat = cats.Item(i);
                    var name = (string)(cat.Name ?? string.Empty);
                    var id = (string)(cat.CategoryID ?? string.Empty);
                    ReleaseComObject(cat);
                    if (id.Equals(DriversClassification, StringComparison.OrdinalIgnoreCase)) return "Driver";
                    if (id.Equals(UpgradesClassification, StringComparison.OrdinalIgnoreCase)) return "Feature upgrade";
                    if (id.Equals(DefinitionUpdatesClassification, StringComparison.OrdinalIgnoreCase))
                        return "Defender";
                    if (name.Equals("Drivers", StringComparison.OrdinalIgnoreCase)) return "Driver";
                    if (name.Equals("Upgrades", StringComparison.OrdinalIgnoreCase)) return "Feature upgrade";
                    if (name.Contains("Servicing", StringComparison.OrdinalIgnoreCase)) return "Servicing";
                }

                if ((int)u.Type == DriverUpdateType) return "Driver";
            }
            catch (COMException ex) { Serilog.Log.Debug(ex, "ClassifyCategory: COM error reading Categories"); }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) { Serilog.Log.Debug(ex, "ClassifyCategory: dynamic binding error"); }
            finally
            {
                // Release the Categories collection even when an early return fires on
                // a match inside the loop (previously the release was skipped on match).
                ReleaseComObject(cats);
            }
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

    /// <summary>Releases a COM object and leaves anything else alone: a test hands in objects of its own.</summary>
    private static void ReleaseComObject(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):F1} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):F1} MB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):F1} KB"),
        > 0 => $"{bytes} B",
        _ => "",
    };
}

/// <summary>Aggregate result of an install batch.</summary>
/// <param name="Installed">The updates that went in.</param>
/// <param name="Failed">The updates whose download or install failed.</param>
/// <param name="RebootRequired">Whether an update that went in needs Windows restarted.</param>
/// <param name="Cancelled">The run stopped on its token, before an update or before an update's install.</param>
/// <param name="AgentError">The HRESULT of the Windows Update Agent error that stopped the run, or 0.</param>
public readonly record struct InstallReport(
    int Installed, int Failed, bool RebootRequired, bool Cancelled = false, int AgentError = 0);
