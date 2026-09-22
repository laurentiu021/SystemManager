// SysManager · SafeFileWalk — the one tree walk every destructive service uses
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// Options for a <see cref="SafeFileWalk"/>. A record rather than optional parameters so a caller cannot
/// silently bind an exclusion path to the search pattern by position.
/// </summary>
internal sealed record SafeWalkOptions
{
    /// <summary>Wildcard the file names must match. <c>*</c> means every file.</summary>
    internal string SearchPattern { get; init; } = "*";

    /// <summary>
    /// Receives every reparse point the walk refused to follow or yield, or null to drop them silently.
    /// </summary>
    /// <remarks>
    /// File Shredder is the one caller that needs them: a folder holding a skipped link is not emptied, and
    /// reporting the shred as complete without saying so would leave the user believing data was destroyed
    /// when it is still there. The cleanup services report a count of what they DID delete, so a link they
    /// left alone needs no separate mention.
    /// </remarks>
    internal ICollection<string>? SkippedLinks { get; init; }

    /// <summary>Directory trees to skip entirely. Null entries are ignored.</summary>
    /// <remarks>
    /// The temp sweeps pass <see cref="SystemPaths.BundleExtractionRoot"/> and
    /// <see cref="SystemPaths.OwnExtractionDirectory"/>: "Temporary files" covers all of %TEMP%, which is
    /// where every single-file .NET app unpacks its native libraries. Deleting those made a clean run report
    /// errors and broke a later lazy load — and excluding only this process's own leaf did that to every
    /// OTHER app under the same root.
    /// </remarks>
    internal IReadOnlyList<string?> ExcludeSubtrees { get; init; } = [];
}

/// <summary>
/// The single reparse-point-safe directory walk. Every service that deletes or overwrites what a walk
/// returns uses this one, so the rules that make a walk safe exist once.
/// </summary>
/// <remarks>
/// There were five hand-copied walkers — in <c>FileShredderService</c>, <c>DeepCleanupService</c> (two),
/// <c>TuneUpService</c> (two), <c>BrowserCleanerService</c> and <c>ShortcutCleanerService</c> — plus four
/// private copies of <c>IsReparsePoint</c>, each carrying a comment saying it mirrored the others. Two
/// separate rules had been added to one copy and not the rest (#2376, #2380):
/// <list type="bullet">
/// <item><description>Only the shredder refused to yield a reparse-point FILE, though every walker refused
/// to descend into a reparse-point DIRECTORY. The delete itself is safe either way — <c>File.Delete</c> on a
/// symlink takes the link — but the size reported for one is the target's, so a bucket could bill the user
/// for bytes no delete reclaimed.</description></item>
/// <item><description>Only <c>DeepCleanupService</c> guarded <c>MoveNext</c>. The enumerator can throw from
/// there rather than from the call that created it, which in <c>TuneUpService</c> escaped the walk and ended
/// the cleanup of an entire temp root on one unreadable entry, reporting a partial figure as a finished
/// clean.</description></item>
/// </list>
/// <para>Attributes come from the enumeration rather than a second <c>GetFileAttributes</c> per entry:
/// <see cref="DirectoryInfo.EnumerateFiles(string)"/> hands back <see cref="FileInfo"/> objects whose
/// attributes are already populated from the directory data the OS returned. The copies this replaces called
/// <c>File.GetAttributes</c> once per subdirectory, so unifying them also removes a syscall per entry.</para>
/// </remarks>
internal static class SafeFileWalk
{
    /// <summary>
    /// Every file under <paramref name="root"/>, depth-first, skipping reparse points — the root, any
    /// directory, and any file — and the excluded subtrees. A per-directory error skips that directory
    /// rather than ending the walk.
    /// </summary>
    internal static IEnumerable<string> Files(string root, CancellationToken ct, SafeWalkOptions options)
    {
        if (!CanEnter(root, options)) yield break;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var current = stack.Pop();
            var dir = new DirectoryInfo(current);

            // Files first, then subdirectories, so a directory that fails to list its children still
            // contributes the files it did list.
            foreach (var file in EnumerateGuarded(() => dir.EnumerateFiles(options.SearchPattern), current))
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    options.SkippedLinks?.Add(file.FullName);
                    continue;
                }
                yield return file.FullName;
            }

            foreach (var sub in EnumerateGuarded(dir.EnumerateDirectories, current))
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    options.SkippedLinks?.Add(sub.FullName);
                    continue;
                }
                if (SystemPaths.IsInsideAnySubtree(sub.FullName, [.. options.ExcludeSubtrees])) continue;
                stack.Push(sub.FullName);
            }
        }
    }

    /// <summary>
    /// Every sub-directory under <paramref name="root"/>, excluding the root itself, deepest first —
    /// the order in which they can be removed once empty. Skips reparse points exactly as
    /// <see cref="Files"/> does, so a cleanup pass can only ever reach directories inside the tree that
    /// was actually walked.
    /// </summary>
    /// <remarks>
    /// Depth is the separator count, not the string length. Both orders happen to work — a child path is
    /// always longer than its own parent — but the separator count says what it means, and a reader should
    /// not have to derive that a length sort is a valid topological order.
    /// </remarks>
    internal static List<string> DirectoriesDeepestFirst(
        string root, CancellationToken ct, SafeWalkOptions options)
    {
        List<string> found = [];
        if (!CanEnter(root, options)) return found;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var dir = new DirectoryInfo(stack.Pop());
            foreach (var sub in EnumerateGuarded(dir.EnumerateDirectories, dir.FullName))
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    options.SkippedLinks?.Add(sub.FullName);
                    continue;
                }
                if (SystemPaths.IsInsideAnySubtree(sub.FullName, [.. options.ExcludeSubtrees])) continue;

                found.Add(sub.FullName);
                stack.Push(sub.FullName);
            }
        }

        found.Sort((a, b) => Depth(b).CompareTo(Depth(a)));
        return found;
    }

    private static int Depth(string path)
    {
        var depth = 0;
        foreach (var c in path)
        {
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) depth++;
        }
        return depth;
    }

    /// <summary>
    /// True when the walk may start at <paramref name="root"/>. Guards the traversal ROOT, not just its
    /// children: a cache path replaced by a junction (writable without admin, e.g.
    /// <c>%LOCALAPPDATA%\NVIDIA\GLCache</c>) would otherwise have its LINK TARGET's files enumerated and
    /// then deleted — data loss outside the tree the caller asked for.
    /// </summary>
    private static bool CanEnter(string root, SafeWalkOptions options) =>
        !IsReparsePoint(root) && !SystemPaths.IsInsideAnySubtree(root, [.. options.ExcludeSubtrees]);

    /// <summary>
    /// Materialises one directory's entries INSIDE a try, absorbing a failure from the call that creates the
    /// enumerator and from any <c>MoveNext</c> during iteration alike. Returns what it managed to read.
    /// </summary>
    /// <remarks>
    /// <c>MoveNext</c> is the half that gets forgotten, and the reason this helper exists at all. Guarding
    /// only the call leaves the throw to escape the walk: measured, not reasoned — <c>EnumerateFiles</c> over
    /// a path that is a FILE returns fine, <c>GetEnumerator</c> returns fine, and <c>MoveNext</c> then threw
    /// <see cref="IOException"/> on all ten attempts without advancing once. In <c>TuneUpService</c>'s copy
    /// that ended the cleanup of an entire temp root on one unreadable entry, while the result still reported
    /// a partial figure as a finished clean (#2380).
    /// <para>A LIST, not a lazy walk, and that is load-bearing rather than incidental: a <c>yield return</c>
    /// cannot sit inside a <c>try</c> that has a <c>catch</c>, so a lazy walk cannot enclose its own
    /// iteration and has to re-derive the guard around every <c>MoveNext</c> — which is exactly the step four
    /// of the five copied walkers skipped. Materialising one directory at a time makes the try the whole
    /// iteration and costs the widest directory, not the tree.</para>
    /// <para>The caller stays lazy: <see cref="Files"/> yields as it goes and holds one directory's entries
    /// at a time, so a walk over %TEMP% never builds a list of every file in it.</para>
    /// </remarks>
    internal static List<T> EnumerateGuarded<T>(Func<IEnumerable<T>> enumerate, string dir)
    {
        List<T> entries = [];
        try
        {
            foreach (var entry in enumerate()) entries.Add(entry);
        }
        catch (IOException ex) { Log.Debug(ex, "SafeFileWalk: stopped listing {Dir}", dir); }
        catch (UnauthorizedAccessException ex) { Log.Debug(ex, "SafeFileWalk: denied listing {Dir}", dir); }
        return entries;
    }

    /// <summary>
    /// True when the path is a reparse point (junction or symbolic link). Fails SAFE: returns true when the
    /// attributes cannot be read, so an unreadable entry is treated as a link and left alone rather than
    /// followed. Used for a path the caller supplied; entries found by the walk carry their attributes
    /// already and are tested directly.
    /// </summary>
    internal static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
