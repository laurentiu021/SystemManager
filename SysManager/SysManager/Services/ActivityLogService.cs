// SysManager · ActivityLogService — persists last N user actions for Dashboard history
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

public sealed class ActivityLogService
{
    /// <summary>
    /// How many entries are kept. Raised from 20 once the destructive operations started logging:
    /// at 20 a busy session could still push a Deep Cleanup or an uninstall out of the history, and
    /// this file is the only record of what the app changed. Each entry is a short action/detail pair,
    /// so the JSON stays a few kilobytes.
    /// </summary>
    internal const int MaxEntries = 60;

    private readonly string _filePath;
    private readonly int _lockAttempts;
    private readonly Lock _lock = new();
    private List<ActivityEntry> _entries = [];

    // Entries logged while the file could not be read, newest first. The next Log that can read the file writes
    // them with it. Writing sooner would replace every entry the file holds with this instance's (#2521).
    private readonly List<ActivityEntry> _unsaved = [];

    /// <summary>
    /// Shared singleton the ViewModels log through. Settable for the same reason
    /// <see cref="DialogService.Instance"/> is: 20+ ViewModel code paths call
    /// <c>ActivityLogService.Instance.Log(...)</c>, and a get-only singleton made the
    /// <see cref="ActivityLogService(string?)"/> seam unreachable from those call sites — so a test
    /// exercising any destructive operation appended to the developer's OWN activity history and, at
    /// <see cref="MaxEntries"/>, evicted every genuine entry. Redirecting the store alone could not
    /// fix that; the call site needs a substitutable instance. See <c>ActivityLogScope</c> in the
    /// test project and issue #1772.
    /// </summary>
    public static ActivityLogService Instance
    {
        get => _instance;
        set => _instance = value ?? throw new ArgumentNullException(nameof(value));
    }

    private static volatile ActivityLogService _instance = new(null);

    /// <summary>
    /// Creates an instance whose store lives under <paramref name="configDir"/>. The production
    /// singleton passes null and resolves the real profile path.
    /// <para>The path was previously <c>static readonly</c>, which made this service impossible to
    /// test: <see cref="Environment.SpecialFolder.LocalApplicationData"/> resolves through the Win32
    /// known-folder API and ignores the <c>LOCALAPPDATA</c> environment variable, so a test calling
    /// <see cref="Log"/> would have written into the user's own activity history. That is why this
    /// seam exists — see the ratchet in ArchitectureTests and issue #1741.</para>
    /// <para><paramref name="lockWait"/> is how long a write waits for another process's; a test that races
    /// writers passes a bound long enough that a stalled runner cannot turn contention into a lost entry.</para>
    /// </summary>
    internal ActivityLogService(string? configDir, TimeSpan? lockWait = null)
    {
        var dir = configDir ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _filePath = Path.Join(dir, "activity.json");
        _lockAttempts = Math.Max(1, (int)((lockWait?.TotalMilliseconds ?? DefaultLockWaitMs) / LockRetryDelayMs));
        Load();
    }

    /// <summary>The newest entries, read from the file, which another process may have written since.</summary>
    /// <remarks>
    /// A command-line or scheduled run is a separate process and records itself in the same file (#1509). The
    /// Dashboard used to show this instance's own list, so such a run never appeared while SysManager stayed
    /// open (#2478). The read needs no cross-process lock: every write replaces the file whole
    /// (<see cref="AtomicFile"/>), so a reader sees the old list or the new one, never half of each.
    /// </remarks>
    public IReadOnlyList<ActivityEntry> GetRecent(int count = 5)
    {
        lock (_lock)
        {
            if (ReadStore().Entries is { } stored) _entries = stored;
            return _unsaved.Concat(_entries).Take(count).ToArray();
        }
    }

    /// <summary>
    /// Records an action. Reads the file, adds the entry and writes it back as one step that no other process
    /// can interleave with.
    /// </summary>
    /// <remarks>
    /// This used to add the entry to the list read at startup and write that list back. When SysManager was
    /// open, which for a tray app is most of the time, the next action it logged overwrote whatever a
    /// command-line or scheduled run had written meanwhile, and that run left no trace anywhere (#2478).
    /// <para>Reading first is not enough on its own: two processes that read before either writes still lose
    /// one entry. <see cref="LockStore"/> closes that.</para>
    /// <para>If the file cannot be read, nothing is written: the entry waits, is listed by <see cref="GetRecent"/>
    /// meanwhile, and is written by the next Log that can read the file. It used to go onto this instance's
    /// list, which was written over the file, so a failed read at startup lost the whole history (#2521). A
    /// file that does not parse is set aside before the first write.</para>
    /// </remarks>
    public void Log(string action, string detail)
    {
        var entry = new ActivityEntry(action, detail, DateTime.Now);
        lock (_lock)
        {
            using var storeLock = LockStore();
            _unsaved.Insert(0, entry);
            if (_unsaved.Count > MaxEntries)
                _unsaved.RemoveRange(MaxEntries, _unsaved.Count - MaxEntries);

            var (stored, unparsable) = ReadStore();
            if (stored is null) return;
            if (unparsable && !SysManager.Helpers.StoreFile.SetAside(_filePath)) return;

            var entries = _unsaved.Concat(stored).Take(MaxEntries).ToList();
            // Written while both locks are held: inside the process, a concurrent Log() cannot mutate the list
            // mid-serialisation; across processes, nobody can write between this read and this write.
            if (!Save(entries)) return;
            _unsaved.Clear();
            _entries = entries;
        }
    }

    /// <summary>
    /// How long, in milliseconds, <see cref="LockStore"/> keeps trying before it writes without the lock. The lock
    /// is held only for one small read and write, so another SysManager process gives it up within milliseconds.
    /// </summary>
    /// <remarks>
    /// Constants, not <c>static readonly TimeSpan</c>s: the constructor reads them, and the <see cref="Instance"/>
    /// singleton is built by a static initializer declared above them. A static field declared later is still
    /// zero at that point, so the production instance would have been given a single attempt.
    /// </remarks>
    internal const int DefaultLockWaitMs = 2000;

    /// <summary>The pause between two tries, in milliseconds.</summary>
    internal const int LockRetryDelayMs = 25;

    /// <summary>
    /// Takes the cross-process lock on the store: an exclusive handle on <c>activity.json.lock</c> beside it.
    /// </summary>
    /// <remarks>
    /// A file beside the store rather than a named mutex, so the lock is bound to the file it protects: a test's
    /// temp directory gets its own, there is no name to derive from a path, and it holds whichever session or
    /// elevation each process runs in. Windows releases the handle when a process ends, so a crash cannot leave
    /// the lock held. Returns null, and the write goes ahead unlocked, if the
    /// lock cannot be had within <see cref="DefaultLockWaitMs"/>: losing a history line is better than losing the
    /// action.
    /// </remarks>
    private FileStream? LockStore()
    {
        var lockPath = _filePath + ".lock";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < _lockAttempts)
            {
                Thread.Sleep(LockRetryDelayMs);   // another process is writing; it will be a few milliseconds
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Debug("ActivityLog: writing without the store lock: {Error}", ex.Message);
                return null;
            }
        }
    }

    private void Load() => _entries = ReadStore().Entries ?? [];

    /// <summary>
    /// The entries in the file: empty if there is no file yet, null if it could not be read. Unparsable is true
    /// when it was read and is not an activity list, and then the entries are empty.
    /// </summary>
    private (List<ActivityEntry>? Entries, bool Unparsable) ReadStore()
    {
        var json = SysManager.Helpers.StoreFile.ReadText(_filePath);
        if (json is null) return (null, false);
        if (json.Length == 0) return ([], false);
        try
        {
            return (JsonSerializer.Deserialize<List<ActivityEntry>>(json) ?? [], false);
        }
        catch (JsonException ex)
        {
            Serilog.Log.Debug("ActivityLog parse failed: {Error}", ex.Message);
            return ([], true);
        }
    }

    // Instance method (was static) because the destination path is now per-instance — that is what
    // lets a test point the store at a temp directory instead of the user's real activity history.
    private bool Save(List<ActivityEntry> snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);
            // WriteIndented = false is the default, so no options object is needed — allocating
            // one per save would only defeat System.Text.Json's per-options metadata cache.
            var json = JsonSerializer.Serialize(snapshot);
            AtomicFile.WriteAllText(_filePath, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug("ActivityLog save failed: {Error}", ex.Message);
            return false;
        }
    }
}
