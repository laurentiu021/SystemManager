// SysManager · SafeFileWalkTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// The walk every destructive service uses, so every rule that makes it safe is tested once.
/// </summary>
/// <remarks>
/// These cases were spread across <c>TuneUpServiceTests</c> and <c>ShortcutCleanerViewModelTests</c>, each
/// exercising one of five hand-copied walkers. Testing the walk in one place is the point of there being one
/// walk: two of those copies were missing rules the others had (#2376, #2380), and per-service tests could
/// not have caught that — each copy passed its own tests.
/// <para>Symlink creation needs Developer Mode or elevation. Those cases skip rather than fail when it is
/// unavailable, which is why the reparse rules are ALSO asserted through <see cref="SafeFileWalk.IsReparsePoint"/>
/// on a path that cannot be read: that half needs no privilege and cannot silently stop running.</para>
/// </remarks>
public class SafeFileWalkTests
{
    private static SafeWalkOptions Plain => new();

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "smwalk_" + Guid.NewGuid().ToString("N"));

    // Creating a symlink needs Developer Mode or elevation. Assert.Skip, not a bare `return`: a silent
    // return is indistinguishable from a pass, so nothing would have told anyone whether these three cases
    // ever ran on any machine. A reported skip shows up in the run summary's `skipped:` count, which is what
    // makes "it runs in CI" a measured fact rather than an assumption.
    private const string NoLinks =
        "creating a symbolic link needs Developer Mode or elevation, and this machine has neither";

    private static bool TryLinkDirectory(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool TryLinkFile(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // ---------- reparse points: directories, the root, and files ----------

    [Fact]
    public void Files_DoesNotFollowADirectorySymlink()
    {
        // Layout: root/real/secret.txt        the "outside" data that must NOT be reached
        //         root/temp/file.txt          a normal file — must be yielded
        //         root/temp/link -> root/real a junction/symlink inside the walked tree
        var root = NewTempRoot();
        var real = Path.Combine(root, "real");
        var temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(real);
        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(real, "secret.txt"), "must never be enumerated");
        File.WriteAllText(Path.Combine(temp, "file.txt"), "ordinary file");

        var link = Path.Combine(temp, "link");
        if (!TryLinkDirectory(link, real)) { Directory.Delete(root, recursive: true); Assert.Skip(NoLinks); }

        try
        {
            List<string> skipped = [];
            var found = SafeFileWalk
                .Files(temp, CancellationToken.None, new SafeWalkOptions { SkippedLinks = skipped })
                .ToList();

            Assert.Contains(found, f => f.EndsWith("file.txt", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(found, f => f.EndsWith("secret.txt", StringComparison.OrdinalIgnoreCase));

            // Refused, not merely absent: the shredder reports these to the user, so a walk that skipped
            // the link without saying so would let it claim a folder was emptied when it was not.
            Assert.Contains(skipped, s => s.EndsWith("link", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            // Remove the link first, without recursing through it, then the tree.
            try { Directory.Delete(link, recursive: false); } catch (IOException) { /* best effort */ }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void Files_DoesNotFollowAReparsePointRoot()
    {
        // The root-not-guarded gap: when the traversal root is ITSELF a link, the walk must yield nothing
        // rather than everything at the link's target. A cache path replaced by a junction is writable
        // without admin, so this is the reachable version of the hazard.
        var baseDir = NewTempRoot();
        var outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "must never be enumerated");

        var rootLink = Path.Combine(baseDir, "rootlink");
        if (!TryLinkDirectory(rootLink, outside)) { Directory.Delete(baseDir, recursive: true); Assert.Skip(NoLinks); }

        try
        {
            Assert.Empty(SafeFileWalk.Files(rootLink, CancellationToken.None, Plain));
            Assert.Empty(SafeFileWalk.DirectoriesDeepestFirst(rootLink, CancellationToken.None, Plain));
        }
        finally
        {
            try { Directory.Delete(rootLink, recursive: false); } catch (IOException) { /* best effort */ }
            try { Directory.Delete(baseDir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void Files_DoesNotYieldAReparsePointFile_AndReportsIt()
    {
        // #2376: only the shredder's copy refused to yield a link FILE, though every copy refused to descend
        // into a link DIRECTORY. File.Delete on a symlink takes the link, so no data was lost — but
        // FileInfo.Length reports the target's size, so a cleanup bucket could bill the user for bytes no
        // delete reclaimed, and the two halves of one helper disagreed about what a link is.
        var root = NewTempRoot();
        var outside = Path.Combine(root, "outside");
        var walked = Path.Combine(root, "walked");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(walked);
        var target = Path.Combine(outside, "target.dat");
        File.WriteAllBytes(target, new byte[4096]);
        File.WriteAllText(Path.Combine(walked, "ordinary.dat"), "a real file");

        var link = Path.Combine(walked, "link.dat");
        if (!TryLinkFile(link, target)) { Directory.Delete(root, recursive: true); Assert.Skip(NoLinks); }

        try
        {
            List<string> skipped = [];
            var found = SafeFileWalk
                .Files(walked, CancellationToken.None, new SafeWalkOptions { SkippedLinks = skipped })
                .ToList();

            Assert.Contains(found, f => f.EndsWith("ordinary.dat", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(found, f => f.EndsWith("link.dat", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(skipped, s => s.EndsWith("link.dat", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { File.Delete(link); } catch (IOException) { /* best effort */ }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void IsReparsePoint_FailsClosedWhenItCannotRead()
    {
        // The privilege-free half of the reparse rules, so the boundary is still under test on a machine
        // where symlink creation is unavailable. An unreadable entry must read as a link and be left alone,
        // never as an ordinary directory to walk into and delete from.
        Assert.True(SafeFileWalk.IsReparsePoint(Path.Combine(NewTempRoot(), "does-not-exist")));
    }

    // ---------- the excluded subtrees (#1999) ----------

    [Fact]
    public void Files_SkipsAnExcludedSubtree_AndKeepsEverythingElse()
    {
        // The shipped exe is single-file with IncludeNativeLibrariesForSelfExtract, so the host unpacks
        // native libraries into %TEMP%\.net\<app>\<hash>. Sweeping all of %TEMP% therefore targeted the
        // running build's own runtime: loaded libraries refused to delete and counted as errors, and
        // anything not yet loaded was deleted for real.
        var root = NewTempRoot();
        var extraction = Path.Combine(root, ".net", "app");
        Directory.CreateDirectory(extraction);
        File.WriteAllText(Path.Combine(root, "ordinary.txt"), "an unrelated temp file");
        File.WriteAllText(Path.Combine(extraction, "native.dll"), "a native library this process needs");

        try
        {
            var found = SafeFileWalk
                .Files(root, CancellationToken.None, new SafeWalkOptions { ExcludeSubtrees = [extraction] })
                .ToList();

            Assert.Contains(found, f => f.EndsWith("ordinary.txt", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(found, f => f.EndsWith("native.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Files_SkipsAnotherAppsExtractionFolder_NotJustOurOwn()
    {
        // The half a leaf exclusion could never cover. Every single-file .NET app unpacks under the SAME
        // root, so excluding only our own <root>\<app>\<hash> spared our native libraries and left every
        // sibling to be deleted — inflicting on another running app exactly the failure our own exclusion
        // exists to prevent, including the "extracted but not yet loaded" case no in-use check can see.
        var root = NewTempRoot();
        var extractionRoot = Path.Combine(root, ".net");
        var ours = Path.Combine(extractionRoot, "ours", "hash");
        var theirs = Path.Combine(extractionRoot, "theirs", "hash");
        Directory.CreateDirectory(ours);
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(root, "ordinary.txt"), "an unrelated temp file");
        File.WriteAllText(Path.Combine(ours, "ours.dll"), "a native library this process needs");
        File.WriteAllText(Path.Combine(theirs, "theirs.dll"), "a native library another app needs");

        try
        {
            var found = SafeFileWalk
                .Files(root, CancellationToken.None,
                    new SafeWalkOptions { ExcludeSubtrees = [extractionRoot, ours] })
                .ToList();

            Assert.Contains(found, f => f.EndsWith("ordinary.txt", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(found, f => f.EndsWith("ours.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(found, f => f.EndsWith("theirs.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Files_WithANullExclusion_StillYieldsEverything()
    {
        // A null among the exclusions must exclude NOTHING rather than everything. Real callers pass
        // SystemPaths.BundleExtractionRoot, which resolves to null on a machine where that path cannot be
        // formed, so getting this backwards would silently turn the temp cleanup into a no-op on exactly
        // those machines.
        var root = NewTempRoot();
        var sub = Path.Combine(root, ".net", "app");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(root, "ordinary.txt"), "x");
        File.WriteAllText(Path.Combine(sub, "native.dll"), "x");

        try
        {
            var found = SafeFileWalk
                .Files(root, CancellationToken.None, new SafeWalkOptions { ExcludeSubtrees = [null] })
                .ToList();

            Assert.Contains(found, f => f.EndsWith("ordinary.txt", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(found, f => f.EndsWith("native.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Files_WhenTheRootItselfIsExcluded_YieldsNothing()
    {
        var root = NewTempRoot();
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "native.dll"), "x");

        try
        {
            Assert.Empty(SafeFileWalk.Files(
                root, CancellationToken.None, new SafeWalkOptions { ExcludeSubtrees = [root] }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- depth, pattern, and a missing root ----------

    [Fact]
    public void Files_FindsFilesAtEveryDepth()
    {
        var root = NewTempRoot();
        var deep = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(root, "top.lnk"), "x");
        File.WriteAllText(Path.Combine(root, "a", "mid.lnk"), "x");
        File.WriteAllText(Path.Combine(deep, "deep.lnk"), "x");

        try
        {
            var found = SafeFileWalk.Files(root, CancellationToken.None, Plain)
                .Select(Path.GetFileName).ToList();

            Assert.Equal(3, found.Count);
            Assert.Contains("top.lnk", found);
            Assert.Contains("mid.lnk", found);
            Assert.Contains("deep.lnk", found);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Files_WithASearchPattern_YieldsOnlyMatches()
    {
        var root = NewTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "keep.lnk"), "x");
        File.WriteAllText(Path.Combine(root, "skip.txt"), "x");
        File.WriteAllText(Path.Combine(root, "sub", "nested.lnk"), "x");

        try
        {
            var found = SafeFileWalk
                .Files(root, CancellationToken.None, new SafeWalkOptions { SearchPattern = "*.lnk" })
                .Select(Path.GetFileName).ToList();

            Assert.Equal(2, found.Count);
            Assert.Contains("keep.lnk", found);
            Assert.Contains("nested.lnk", found);
            Assert.DoesNotContain("skip.txt", found);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Files_MissingRoot_ReturnsEmptyWithoutThrowing()
    {
        // A scan location that does not exist on this machine must be a no-op, not an exception that ends
        // the caller's whole scan. Reached through IsReparsePoint failing closed on an unreadable path.
        Assert.Empty(SafeFileWalk.Files(
            Path.Combine(NewTempRoot(), "nope"), CancellationToken.None, Plain));
    }

    [Fact]
    public void DirectoriesDeepestFirst_OrdersByDepth_NotByNameLength()
    {
        // The cleanup passes delete only EMPTY directories, so a parent handed over before its child simply
        // fails and leaves an empty directory behind. Depth is the separator count: a length sort happens to
        // work, because a child path is always longer than its own parent, but it does not say so.
        var root = NewTempRoot();
        var shallowButLongName = Path.Combine(root, "a-directory-with-a-very-long-name");
        var deep = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(shallowButLongName);
        Directory.CreateDirectory(deep);

        try
        {
            var order = SafeFileWalk.DirectoriesDeepestFirst(root, CancellationToken.None, Plain);

            Assert.Equal(4, order.Count);   // the long-named one, plus a, a\b, a\b\c
            Assert.Equal(deep, order[0]);   // deepest first, though it is not the longest string

            // And every directory precedes its own parent, which is the property the delete depends on.
            for (var i = 0; i < order.Count; i++)
            {
                var parent = Path.GetDirectoryName(order[i])!;
                var parentAt = order.IndexOf(parent);
                Assert.True(parentAt < 0 || parentAt > i,
                    $"{order[i]} was listed after its parent, so removing the parent would fail and leave it behind");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- the MoveNext guard (#2380) ----------

    [Fact]
    public void EnumerateGuarded_KeepsWhatItReadBeforeMoveNextThrew()
    {
        // The half that gets forgotten. The enumerator can throw from MoveNext rather than from the call
        // that created it, and only one of the five copied walkers guarded it — in TuneUpService the throw
        // escaped the walk and ended the cleanup of an entire temp root on one unreadable entry, while the
        // result still reported a partial figure as a finished clean.
        var entries = SafeFileWalk.EnumerateGuarded(() => ThrowAfter(2), "test");

        Assert.Equal(["first", "second"], entries);
    }

    [Fact]
    public void EnumerateGuarded_DoesNotRetryAnEnumeratorThatThrew()
    {
        // A throwing MoveNext leaves the enumerator terminal, so asking it again re-throws the same exception
        // forever — that is how an earlier `catch { continue; }` in this position pinned a core in a loop
        // cancellation could not reach. Counting the attempts is what proves the walk moved on.
        var calls = 0;
        var entries = SafeFileWalk.EnumerateGuarded(() => CountingThrow(() => calls++), "test");

        Assert.Empty(entries);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void EnumerateGuarded_AbsorbsAFailureFromTheCallItself()
    {
        var entries = SafeFileWalk.EnumerateGuarded<string>(
            () => throw new UnauthorizedAccessException("cannot list"), "test");

        Assert.Empty(entries);
    }

    private static IEnumerable<string> ThrowAfter(int count)
    {
        var names = new[] { "first", "second", "third" };
        for (var i = 0; i < names.Length; i++)
        {
            if (i == count) throw new IOException("the directory stopped being readable");
            yield return names[i];
        }
    }

    private static IEnumerable<string> CountingThrow(Action onMoveNext)
    {
        onMoveNext();
        throw new IOException("unreadable from the first step");
#pragma warning disable CS0162 // Unreachable: the iterator needs a yield to be an iterator at all.
        yield break;
#pragma warning restore CS0162
    }
}
