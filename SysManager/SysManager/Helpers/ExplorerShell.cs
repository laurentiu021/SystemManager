// SysManager · ExplorerShell
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// Single source of truth for stopping, starting and restarting the Windows shell, and for the
/// icon/thumbnail cache files that can only be deleted while it is stopped.
/// </summary>
/// <remarks>
/// Two tabs restart Explorer — Context Menu, to apply a menu-style change, and System Fixes, because
/// "my taskbar is frozen" and "my desktop disappeared" are what a user actually reports. Routing both
/// through one helper keeps the kill loop from drifting between copies, the same reason
/// <see cref="RecycleBinHelper"/> exists.
/// <para><see cref="Stop"/> and <see cref="Start"/> are separate from <see cref="Restart"/> because
/// rebuilding the icon cache has to happen BETWEEN them: Explorer holds those .db files open, so a
/// delete attempted while it runs fails on exactly the files that are caching the wrong icons.</para>
/// </remarks>
public static class ExplorerShell
{
    /// <summary>
    /// The cache files Windows rebuilds by itself, and the only ones this helper deletes.
    /// </summary>
    /// <remarks>
    /// A property rather than a field so callers cannot mutate a shared array. Deep Cleanup's
    /// "Explorer thumbnail & icon cache" category uses this same list; its directory, though, comes from
    /// its own injected roots rather than from <see cref="CacheDirectory"/>, because that seam is what
    /// makes its scan testable.
    /// </remarks>
    public static string[] CacheFilePatterns => ["thumbcache_*.db", "iconcache_*.db"];

    /// <summary>Explorer's own working folder, where the caches live.</summary>
    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Explorer");

    /// <summary>
    /// Ends every Explorer instance. Each process is killed individually so one unkillable instance
    /// (a higher-integrity or other-session explorer) cannot abort the loop and leave the user
    /// without a shell.
    /// </summary>
    public static void Stop()
    {
        foreach (var proc in Process.GetProcessesByName("explorer"))
        {
            try
            {
                proc.Kill();
                proc.WaitForExit(3000);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Debug("Could not kill explorer PID {Pid}: {Error}", proc.Id, ex.Message);
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    /// <summary>Relaunches Explorer. Safe to call when one is already running — Windows reuses it.</summary>
    public static void Start()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SystemPaths.ResolveSystemTool("explorer.exe")) { UseShellExecute = true })?.Dispose();
            Log.Information("Explorer started");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning("Failed to relaunch Explorer: {Error}", ex.Message);
        }
    }

    /// <summary>Stops every Explorer instance and brings one back.</summary>
    public static void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>What one sweep of the cache folder managed to remove.</summary>
    public readonly record struct CacheSweep(int Deleted, int Failed, long BytesFreed);

    /// <summary>
    /// Deletes the icon and thumbnail cache databases in <paramref name="directory"/>.
    /// </summary>
    /// <param name="directory">
    /// Defaults to <see cref="CacheDirectory"/>. A parameter so the sweep is testable against a temp
    /// folder — the process control above is not, and keeping them separate is the point.
    /// </param>
    /// <remarks>
    /// Only <see cref="CacheFilePatterns"/> are touched. Explorer's folder holds unrelated state next to
    /// the caches, so a blanket delete would remove things Windows does NOT rebuild.
    /// <para>Never throws for an individual file. A file still locked is counted in
    /// <see cref="CacheSweep.Failed"/> and reported, because the caller has to relaunch the shell either
    /// way — leaving the user without a desktop to report an <see cref="IOException"/> would be a far
    /// worse outcome than a partial sweep.</para>
    /// <para>Read-only and hidden attributes are cleared first: Windows marks some cache databases
    /// hidden, and <see cref="File.Delete(string)"/> refuses a read-only file.</para>
    /// <para>A missing folder is handled TWICE on purpose, and both are worth keeping: the
    /// <see cref="Directory.Exists"/> check below returns early with a clear log line, and the
    /// <see cref="IOException"/> catch around <see cref="Directory.GetFiles(string, string)"/> would
    /// catch the <see cref="DirectoryNotFoundException"/> anyway. Neither is redundant to a reader —
    /// removing the early return turns a normal condition into exception-driven flow, and removing the
    /// catch loses the case where the folder disappears between the check and the listing. A mutation run
    /// confirmed that deleting either one alone leaves the behaviour, and therefore the test, unchanged.</para>
    /// </remarks>
    public static CacheSweep DeleteCacheFiles(string? directory = null)
    {
        var dir = directory ?? CacheDirectory;
        if (!Directory.Exists(dir))
        {
            Log.Debug("Explorer cache folder not found at {Dir}", dir);
            return new CacheSweep(0, 0, 0);
        }

        var deleted = 0;
        var failed = 0;
        long bytes = 0;

        foreach (var pattern in CacheFilePatterns)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, pattern);
            }
            catch (IOException ex)
            {
                Log.Debug(ex, "Could not list {Pattern} in {Dir}", pattern, dir);
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Debug(ex, "Access denied listing {Pattern} in {Dir}", pattern, dir);
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    // Counted only after the delete returns, so a locked file is never reported as freed.
                    deleted++;
                    bytes += size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    Log.Debug(ex, "Could not delete cache file {File}", file);
                }
            }
        }

        Log.Information("Explorer cache sweep: {Deleted} deleted, {Failed} still locked, {Bytes} bytes",
            deleted, failed, bytes);
        return new CacheSweep(deleted, failed, bytes);
    }
}
