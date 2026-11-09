// SysManager · OldBuildFolders
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.RegularExpressions;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// Deletes the folders older builds of SysManager unpacked their native libraries into (#2858).
/// </summary>
/// <remarks>
/// <para>The .NET host unpacks a single-file app's native libraries into
/// <c>&lt;root&gt;\&lt;exe name&gt;\&lt;bundle id&gt;</c> under <see cref="SystemPaths.BundleExtractionRoot"/> and
/// never deletes them, and the temp sweeps leave that root alone on purpose, so every build that ever ran kept about
/// 26 MB there for good: a <c>SysManager-v&lt;version&gt;</c> folder for each release run as downloaded, and one more
/// bundle id under the exe's name for each build of an exe that keeps its name across versions, as winget's
/// <c>SysManager.exe</c> does.</para>
/// <para>What goes: a <c>SysManager-v&lt;version&gt;</c> folder of an older version than this build, and a bundle id
/// beside this build's own. What stays: this build's own folder and anything newer; a folder written to in the last
/// <see cref="MinimumAge"/>, which a build starting now may be unpacking into or loading from; a folder that is,
/// is reached through or holds a link; a folder that holds files but no <see cref="InUseMarker"/>, which is not
/// recognisably SysManager's; and a folder whose <see cref="InUseMarker"/> cannot be deleted, which a running copy has
/// loaded. That file goes first, so a folder in use loses nothing.</para>
/// <para>Only when SysManager is not elevated. The root is in the user's profile, and any program running as the user
/// can write to it, so deleting there as an administrator would let that program choose what an administrator process
/// deletes. Run as the user, the delete can do nothing that program could not do itself.</para>
/// </remarks>
internal static partial class OldBuildFolders
{
    /// <summary>How long after its last write a folder is left alone.</summary>
    internal static readonly TimeSpan MinimumAge = TimeSpan.FromDays(7);

    /// <summary>
    /// WPF's renderer, unpacked by every build and loaded by every copy with a window. Windows refuses to delete a
    /// library a process has loaded, so a folder whose copy of it will not go is one a running build uses.
    /// </summary>
    internal const string InUseMarker = "wpfgfx_cor3.dll";

    /// <summary>The folder name a release's exe unpacks under: <c>SysManager-v1.2.3</c>.</summary>
    [GeneratedRegex(@"^SysManager-v(\d+\.\d+\.\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseFolderName();

    /// <summary>What one pass did.</summary>
    /// <param name="Removed">Folders deleted.</param>
    /// <param name="Bytes">The size of the files deleted with them.</param>
    /// <param name="Kept">Folders that qualified by name and were left: too recent, in use, holding a link, not
    /// recognisably SysManager's, or not wholly deleted.</param>
    internal readonly record struct Outcome(int Removed, long Bytes, int Kept);

    /// <summary>
    /// The pass the app runs once its window is up: this build's folders, the time now, and whether it is elevated.
    /// </summary>
    internal static Outcome RemoveForThisBuild() =>
        Remove(SystemPaths.BundleExtractionRoot, SystemPaths.OwnExtractionDirectory,
            typeof(OldBuildFolders).Assembly.GetName().Version ?? new Version(0, 0),
            TimeProvider.System.GetUtcNow().UtcDateTime, AdminHelper.IsElevated());

    /// <summary>
    /// Deletes the old build folders under <paramref name="root"/>, given this build's own unpacked folder, its
    /// version, the time now and whether the process is elevated.
    /// </summary>
    internal static Outcome Remove(string? root, string? own, Version running, DateTime nowUtc, bool elevated)
    {
        if (elevated) return default;

        int removed = 0, kept = 0;
        long bytes = 0;
        foreach (var folder in Candidates(root, own, running))
        {
            if (TryRemove(folder, nowUtc, out var freed))
            {
                removed++;
                bytes += freed;
            }
            else
            {
                kept++;
            }
        }

        if (removed > 0)
        {
            Log.Information("Old build folders: removed {Removed}, {Megabytes:F1} MB; left {Kept}",
                removed, bytes / 1048576.0, kept);
        }
        else if (kept > 0)
        {
            Log.Debug("Old build folders: none removed; left {Kept}", kept);
        }
        return new Outcome(removed, bytes, kept);
    }

    /// <summary>
    /// The folders that qualify by name: each <c>SysManager-v&lt;version&gt;</c> folder under <paramref name="root"/>
    /// older than <paramref name="running"/>, and each bundle id beside <paramref name="own"/>. Empty unless
    /// <paramref name="own"/> is <c>&lt;root&gt;\&lt;exe name&gt;\&lt;bundle id&gt;</c> and the root is a real folder
    /// reached through no link.
    /// </summary>
    internal static List<string> Candidates(string? root, string? own, Version running)
    {
        List<string> found = [];
        if (root is null || own is null) return found;
        var exeFolder = Path.GetDirectoryName(own);
        if (exeFolder is null || !Same(Path.GetDirectoryName(exeFolder), root)) return found;
        if (!Directory.Exists(root) || SafeFileWalk.IsReachedThroughLink(root)) return found;

        // Three parts, as the folder names have: the assembly's fourth, 0, would put this build's own name below it.
        var current = new Version(running.Major, running.Minor, Math.Max(running.Build, 0));
        foreach (var dir in SafeFileWalk.EnumerateGuarded(new DirectoryInfo(root).EnumerateDirectories, root))
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || Same(dir.FullName, exeFolder)) continue;
            var name = ReleaseFolderName().Match(dir.Name);
            if (name.Success && Version.TryParse(name.Groups[1].Value, out var version) && version < current)
                found.Add(dir.FullName);
        }

        foreach (var dir in SafeFileWalk.EnumerateGuarded(new DirectoryInfo(exeFolder).EnumerateDirectories, exeFolder))
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || Same(dir.FullName, own)) continue;
            found.Add(dir.FullName);
        }
        return found;
    }

    /// <summary>
    /// Deletes <paramref name="folder"/> when it is old enough, holds no link, is recognisably SysManager's and is not
    /// in use; false when it is left, wholly or in part.
    /// </summary>
    private static bool TryRemove(string folder, DateTime nowUtc, out long freed)
    {
        freed = 0;
        if (SafeFileWalk.IsReachedThroughLink(folder)) return false;

        List<string> links = [];
        var walk = new SafeWalkOptions { SkippedLinks = links };
        List<string> files = [.. SafeFileWalk.Files(folder, CancellationToken.None, walk)];
        var dirs = SafeFileWalk.DirectoriesDeepestFirst(folder, CancellationToken.None, walk);
        if (links.Count > 0) return false;

        var newest = LastWriteUtc(folder);
        foreach (var path in files.Concat(dirs))
        {
            var written = LastWriteUtc(path);
            if (written > newest) newest = written;
        }
        if (nowUtc - newest < MinimumAge) return false;

        // The in-use check. Every build unpacks the marker, so a folder holding files but none is not recognisably
        // SysManager's, and a marker that will not delete is one a running copy has loaded: stop before anything else
        // in that folder goes.
        var markers = files
            .Where(f => string.Equals(Path.GetFileName(f), InUseMarker, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (files.Count > 0 && markers.Count == 0) return false;
        foreach (var marker in markers)
        {
            if (!TryDeleteFile(marker, ref freed)) return false;
        }

        var whole = true;
        foreach (var file in files.Except(markers)) whole &= TryDeleteFile(file, ref freed);
        foreach (var dir in dirs) whole &= TryDeleteDirectory(dir);
        return TryDeleteDirectory(folder) && whole;
    }

    private static bool TryDeleteFile(string path, ref long freed)
    {
        try
        {
            var size = new FileInfo(path).Length;
            File.Delete(path);
            freed += size;
            return true;
        }
        catch (IOException ex) { Kept(path, ex); }
        catch (UnauthorizedAccessException ex) { Kept(path, ex); }
        return false;
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (IOException ex) { Kept(path, ex); }
        catch (UnauthorizedAccessException ex) { Kept(path, ex); }
        return false;
    }

    private static void Kept(string path, Exception reason) =>
        Log.Debug("Old build folders: kept {Path}: {Error}", path, reason.Message);

    /// <summary>
    /// When <paramref name="path"/> was last written, or the latest time there is if that cannot be read.
    /// </summary>
    /// <remarks>Fails safe: an entry whose time cannot be read makes its folder too recent to delete.</remarks>
    private static DateTime LastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.MaxValue; }
        catch (UnauthorizedAccessException) { return DateTime.MaxValue; }
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && SystemPaths.IsInsideSubtree(a, b) && SystemPaths.IsInsideSubtree(b, a);
}
