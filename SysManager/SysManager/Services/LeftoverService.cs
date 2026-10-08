// SysManager · LeftoverService — finds and removes what an uninstall left behind
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>What removing the ticked leftovers did.</summary>
/// <param name="Removed">The items that are gone: sent to the Recycle Bin, deleted after a backup, or already gone.</param>
/// <param name="BytesFreed">
/// The size of the folders SysManager sent to the Recycle Bin, as measured when they were found, with a folder inside
/// another counted once.
/// </param>
/// <param name="Errors">One sentence per item that was not removed, naming it and why.</param>
public sealed record LeftoverRemoval(IReadOnlyList<LeftoverItem> Removed, long BytesFreed, IReadOnlyList<string> Errors);

/// <summary>Finds what an uninstall left behind, and removes the items the user ticks (#1527).</summary>
public interface ILeftoverService
{
    /// <summary>What <paramref name="probe"/>'s app left behind, with each folder's size. Runs off the UI thread.</summary>
    Task<LeftoverGroup> FindAsync(UninstallProbe probe, IReadOnlyCollection<UninstallProbe> stillInstalled,
                                  CancellationToken ct = default);

    /// <summary>
    /// Sends the folders to the Recycle Bin and deletes the keys after exporting each to a <c>.reg</c> file. Every
    /// folder is checked again first, so one that turned into a link, or gained one, since it was found is refused.
    /// </summary>
    Task<LeftoverRemoval> RemoveAsync(IReadOnlyList<LeftoverItem> items, CancellationToken ct = default);

    /// <summary>
    /// The items an earlier session could not remove for want of administrator rights, checked against the PC again:
    /// what is gone, now refused, now used by an installed app, or left by an app installed again since is dropped,
    /// from the list and from the record. None arrives ticked. Empty when there are none or the record cannot be read.
    /// </summary>
    IReadOnlyList<LeftoverGroup> LoadPending();

    /// <summary>Adds the items in <paramref name="groups"/> that need administrator rights to the record.</summary>
    void RememberPending(IReadOnlyList<LeftoverGroup> groups);

    /// <summary>Drops <paramref name="folders"/> from the record, and the record itself once it is empty.</summary>
    void ForgetPending(IReadOnlyCollection<string> folders);
}

/// <summary>The leftover search and removal behind the Uninstaller's "Left behind" card.</summary>
/// <remarks>
/// <para><b>Folders go to the Recycle Bin</b> through <see cref="RecycleBinHelper.SendToRecycleBin"/>, so a wrong
/// guess can be put back. A folder that holds a link anywhere inside is never offered: what the Recycle Bin does with
/// a link it is handed is the shell's business, and a leftover is not worth finding out. <b>Keys are exported
/// first</b>, with <c>reg.exe export</c> into <c>%LocalAppData%\SysManager\Backups\Uninstaller</c>, and deleted only
/// once the export exists; only keys under <c>HKEY_CURRENT_USER\Software</c> are ever offered.</para>
/// <para><b>What needs administrator rights is remembered.</b> Uninstalling runs without them, so a leftover under
/// Program Files or ProgramData usually cannot be removed in the same session. Those items are kept in
/// <c>%LocalAppData%\SysManager\uninstaller-leftovers.json</c> and offered again when the Uninstaller is opened in a
/// session that runs as administrator.</para>
/// </remarks>
public sealed class LeftoverService : ILeftoverService
{
    /// <summary>How many exports are kept for one key, as Context Menu keeps.</summary>
    private const int BackupsKeptPerKey = ContextMenuService.BackupsKeptPerKey;

    /// <summary>
    /// Confidence is written by name, so the record still reads correctly if the enum is ever reordered.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<LeftoverConfidence>() },
    };

    private readonly IPowerShellRunner _ps;
    private readonly ILeftoverEnvironment _env;
    private readonly string _pendingPath;
    private readonly string _backupDir;
    private readonly Func<string, bool> _recycle;
    private readonly Func<string, bool> _deleteKey;

    /// <summary>
    /// Held across each read and write of the record: an uninstall run remembering items while the card forgets the
    /// ones just removed would otherwise each write back a copy taken before the other landed.
    /// </summary>
    private readonly Lock _pendingLock = new();

    /// <summary>The service production uses: the real folders and registry.</summary>
    public LeftoverService(IPowerShellRunner ps)
        : this(ps, new SystemLeftoverEnvironment(), RecycleBinHelper.SendToRecycleBin, DeleteCurrentUserKey)
    {
    }

    /// <summary>A service over <paramref name="env"/>, so a test supplies the folders, the registry and both removals.</summary>
    /// <param name="ps">Runs <c>reg.exe export</c>.</param>
    /// <param name="env">Where to look and what is never offered; its <c>LocalAppData</c> also holds the record and backups.</param>
    /// <param name="recycle">
    /// Sends a folder to the Recycle Bin. Defaults to refusing, so a test that leaves it out cannot send anything of
    /// the PC running it to the bin.
    /// </param>
    /// <param name="deleteKey">Deletes a key under <c>HKEY_CURRENT_USER</c>. Defaults to refusing, for the same reason.</param>
    internal LeftoverService(IPowerShellRunner ps, ILeftoverEnvironment env,
                             Func<string, bool>? recycle = null, Func<string, bool>? deleteKey = null)
    {
        _ps = ps ?? throw new ArgumentNullException(nameof(ps));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        var home = Path.Combine(env.LocalAppData, "SysManager");
        _pendingPath = Path.Combine(home, "uninstaller-leftovers.json");
        _backupDir = Path.Combine(home, "Backups", "Uninstaller");
        _recycle = recycle ?? (_ => false);
        _deleteKey = deleteKey ?? (_ => false);
    }

    /// <summary>The folder the <c>.reg</c> backups are written to.</summary>
    internal string BackupDirectory => _backupDir;

    /// <summary>The record of what is waiting for administrator rights.</summary>
    internal string PendingPath => _pendingPath;

    public Task<LeftoverGroup> FindAsync(UninstallProbe probe, IReadOnlyCollection<UninstallProbe> stillInstalled,
                                         CancellationToken ct = default)
        => Task.Run(() =>
        {
            var findings = LeftoverFinder.Find(probe, _env, stillInstalled);
            var items = new List<LeftoverItem>();
            var notOffered = new List<string>(findings.NotOffered);
            foreach (var item in findings.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (item.Kind == LeftoverKind.Folder)
                {
                    var (bytes, holdsLink) = MeasureFolder(item.Location, ct);
                    if (holdsLink)
                    {
                        notOffered.Add($"Not offered: {item.Location} holds a link to another folder.");
                        continue;
                    }
                    item.SizeBytes = bytes;
                }
                items.Add(item);
            }

            return new LeftoverGroup
            {
                AppName = probe.Name,
                Publisher = probe.Publisher,
                Items = new ObservableCollection<LeftoverItem>(items),
                NotOffered = notOffered,
            };
        }, ct);

    public Task<LeftoverRemoval> RemoveAsync(IReadOnlyList<LeftoverItem> items, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var removed = new List<LeftoverItem>();
            var recycled = new List<LeftoverItem>();
            var errors = new List<string>();

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (item.Kind == LeftoverKind.Folder)
                {
                    switch (RemoveFolder(item, errors, ct))
                    {
                        case FolderOutcome.Recycled: removed.Add(item); recycled.Add(item); break;
                        case FolderOutcome.AlreadyGone: removed.Add(item); break;
                    }
                }
                else if (await RemoveKeyAsync(item, errors, ct).ConfigureAwait(false))
                {
                    removed.Add(item);
                }
            }

            return new LeftoverRemoval(removed, LeftoverFinder.DistinctBytes(recycled), errors);
        }, ct);

    private enum FolderOutcome { Recycled, AlreadyGone, Kept }

    private FolderOutcome RemoveFolder(LeftoverItem item, List<string> errors, CancellationToken ct)
    {
        // Gone already, most often because a folder that held it went first: nothing is left to remove.
        if (!Directory.Exists(item.Location)) return FolderOutcome.AlreadyGone;

        // Checked again now rather than trusted from the search: a folder can be swapped for a link in between,
        // or have one put inside it. The shell takes a path, so the moment between this check and its move is not
        // closed; it is the same window File Shredder documents, kept as small as doing the check last makes it.
        var why = LeftoverFinder.Refusal(item.Location, _env)
            ?? (MeasureFolder(item.Location, ct).HoldsLink ? "holds a link to another folder" : null);
        if (why is not null)
        {
            errors.Add($"{item.Location}: not removed, because it {why}.");
            return FolderOutcome.Kept;
        }

        if (_recycle(item.Location)) return FolderOutcome.Recycled;
        errors.Add($"{item.Location}: Windows did not send it to the Recycle Bin.");
        return FolderOutcome.Kept;
    }

    private async Task<bool> RemoveKeyAsync(LeftoverItem item, List<string> errors, CancellationToken ct)
    {
        var shown = item.DisplayLocation;
        if (!LeftoverFinder.IsSafeKeyPath(item.Location))
        {
            errors.Add($"{shown}: not removed, because its name cannot be passed safely to reg.exe.");
            return false;
        }
        if (!_env.RegistryKeyExists(item.Location)) return true;

        var backup = await ExportKeyAsync(item.Location, ct).ConfigureAwait(false);
        if (backup is null)
        {
            errors.Add($"{shown}: not removed, because its backup copy could not be saved.");
            return false;
        }

        if (_deleteKey(item.Location))
        {
            Log.Information("Uninstaller leftovers: deleted {Key}, backup {Backup}", shown, backup);
            return true;
        }
        errors.Add($"{shown}: Windows did not let SysManager delete it. Its backup is in {backup}.");
        return false;
    }

    /// <summary>Exports a key to a new <c>.reg</c> file and returns its path, or null when no export was written.</summary>
    private async Task<string?> ExportKeyAsync(string subKey, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_backupDir);
            var safeName = string.Join("_", subKey.Split(Path.GetInvalidFileNameChars()));
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var file = Path.Combine(_backupDir, $"{safeName}_{stamp}.reg");
            var exit = await _ps.RunProcessAsync("reg.exe", $"export \"HKCU\\{subKey}\" \"{file}\" /y", ct)
                .ConfigureAwait(false);
            if (exit != 0 || !File.Exists(file) || new FileInfo(file).Length == 0) return null;

            ContextMenuService.PruneBackups(_backupDir, safeName, BackupsKeptPerKey);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warning("Uninstaller leftovers: exporting {Key} failed: {Error}", subKey, ex.Message);
            return null;
        }
    }

    public IReadOnlyList<LeftoverGroup> LoadPending()
    {
        lock (_pendingLock)
        {
            var stored = ReadStored();
            if (stored.Count == 0) return [];

            // The record is only ever read in a session that runs as administrator, which has never seen the
            // Uninstaller's list; the uninstall entries Windows holds are what decides whether a folder is in use now.
            var others = LeftoverFinder.OthersStillInstalled([], _env);
            var groups = new List<LeftoverGroup>();
            var kept = new List<StoredGroup>();
            foreach (var group in stored)
            {
                // An app installed again since is not gone any more, so nothing it left is a leftover now either.
                if (others.Any(o => string.Equals(o.Name, group.AppName, StringComparison.OrdinalIgnoreCase))) continue;

                var items = new ObservableCollection<LeftoverItem>();
                var keptFolders = new List<StoredFolder>();
                foreach (var folder in group.Folders)
                {
                    // Only what is still there and still allowed is offered again; the size is measured now.
                    if (!Directory.Exists(folder.Path)) continue;
                    if (LeftoverFinder.Refusal(folder.Path, _env) is not null) continue;
                    if (LeftoverFinder.SharedWith(folder.Path, others, _env) is not null) continue;
                    // Found by its name, so refused again by its name, exactly as the search refused it, when an app
                    // answering to that name has been installed since.
                    if (folder.Confidence == LeftoverConfidence.Probably
                        && LeftoverFinder.IsShared(Path.GetFileName(folder.Path), others)) continue;
                    var (bytes, holdsLink) = MeasureFolder(folder.Path, CancellationToken.None);
                    if (holdsLink) continue;

                    // Nothing arrives ticked, Certain included. The record sits in the user's own profile, where
                    // anything running as the user could have rewritten it since; a session with administrator
                    // rights removes only what the user ticks in it.
                    keptFolders.Add(folder);
                    items.Add(new LeftoverItem
                    {
                        Location = folder.Path,
                        Kind = LeftoverKind.Folder,
                        Confidence = folder.Confidence,
                        Publisher = folder.Publisher,
                        NeedsAdministrator = LeftoverFinder.NeedsAdministrator(folder.Path, _env),
                        SizeBytes = bytes,
                    });
                }

                if (keptFolders.Count == 0) continue;
                kept.Add(group with { Folders = keptFolders });
                groups.Add(new LeftoverGroup { AppName = group.AppName, Publisher = group.Publisher, Items = items });
            }

            if (kept.Count != stored.Count || kept.Sum(g => g.Folders.Count) != stored.Sum(g => g.Folders.Count))
                WriteStored(kept);
            return groups;
        }
    }

    public void RememberPending(IReadOnlyList<LeftoverGroup> groups)
    {
        lock (_pendingLock)
        {
            var stored = ReadStored().ToList();
            var added = false;
            foreach (var group in groups)
            {
                var folders = group.Items
                    .Where(i => i.NeedsAdministrator && i.Kind == LeftoverKind.Folder)
                    .Where(i => !stored.Any(s => s.Folders.Any(f => string.Equals(f.Path, i.Location, StringComparison.OrdinalIgnoreCase))))
                    .Select(i => new StoredFolder(i.Location, i.Confidence, i.Publisher))
                    .ToList();
                if (folders.Count == 0) continue;

                stored.Add(new StoredGroup(group.AppName, group.Publisher, folders));
                added = true;
            }
            if (added) WriteStored(stored);
        }
    }

    public void ForgetPending(IReadOnlyCollection<string> folders)
    {
        lock (_pendingLock)
        {
            var stored = ReadStored();
            if (stored.Count == 0 || folders.Count == 0) return;
            var kept = stored
                .Select(s => s with
                {
                    Folders = [.. s.Folders.Where(f => !folders.Contains(f.Path, StringComparer.OrdinalIgnoreCase))],
                })
                .Where(s => s.Folders.Count > 0)
                .ToList();
            WriteStored(kept);
        }
    }

    private IReadOnlyList<StoredGroup> ReadStored()
    {
        try
        {
            return File.Exists(_pendingPath) ? Parse(File.ReadAllText(_pendingPath)) : [];
        }
        catch (IOException ex) { Log.Debug("Uninstaller leftovers: pending record unreadable: {Error}", ex.Message); return []; }
        catch (UnauthorizedAccessException ex) { Log.Debug("Uninstaller leftovers: pending record denied: {Error}", ex.Message); return []; }
    }

    private void WriteStored(IReadOnlyList<StoredGroup> stored)
    {
        try
        {
            if (stored.Count == 0)
            {
                if (File.Exists(_pendingPath)) File.Delete(_pendingPath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_pendingPath)!);
            AtomicFile.WriteAllText(_pendingPath, JsonSerializer.Serialize(stored, JsonOptions));
        }
        catch (IOException ex) { Log.Warning("Uninstaller leftovers: pending record not saved: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warning("Uninstaller leftovers: pending record denied: {Error}", ex.Message); }
    }

    /// <summary>One remembered folder, with how sure the search was and, for a guess, the publisher it was named after.</summary>
    internal sealed record StoredFolder(string Path, LeftoverConfidence Confidence, string Publisher);

    /// <summary>One app's remembered items. Folders only: no key is ever under HKLM, so none needs administrator.</summary>
    internal sealed record StoredGroup(string AppName, string Publisher, List<StoredFolder> Folders);

    /// <summary>The stored groups, or none for a record that is empty, malformed or from a future shape.</summary>
    internal static IReadOnlyList<StoredGroup> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var groups = JsonSerializer.Deserialize<List<StoredGroup>>(json, JsonOptions);
            return groups?
                .Where(g => g is { AppName.Length: > 0, Folders: not null })
                .Select(g => g with
                {
                    Publisher = g.Publisher ?? "",
                    Folders = [.. g.Folders.Where(f => f is { Path.Length: > 0 }).Select(f => f with { Publisher = f.Publisher ?? "" })],
                })
                .Where(g => g.Folders.Count > 0)
                .ToList() ?? [];
        }
        catch (JsonException ex)
        {
            Log.Debug("Uninstaller leftovers: pending record malformed: {Error}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// The bytes in a folder, walked without following links as Deep Cleanup walks, and whether a link was met on the
    /// way: a folder holding one is never offered.
    /// </summary>
    private static (long Bytes, bool HoldsLink) MeasureFolder(string path, CancellationToken ct)
    {
        var links = new List<string>();
        long bytes = 0;
        foreach (var file in SafeFileWalk.Files(path, ct, new SafeWalkOptions { SkippedLinks = links }))
        {
            try { bytes += new FileInfo(file).Length; }
            catch (IOException) { /* gone or locked mid-walk: counts as nothing */ }
            catch (UnauthorizedAccessException) { /* unreadable: counts as nothing */ }
        }
        return (bytes, links.Count > 0);
    }

    private static bool DeleteCurrentUserKey(string subKey)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                      or IOException or ArgumentException)
        {
            Log.Warning("Uninstaller leftovers: deleting HKCU\\{Key} failed: {Error}", subKey, ex.Message);
            return false;
        }
    }
}
