// SysManager · Symlinks — one place that knows how a test asks for a link
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;

namespace SysManager.Tests;

/// <summary>
/// Creates the links a reparse-point test needs, and skips the test — loudly — when this machine cannot.
/// </summary>
/// <remarks>
/// Creating a symbolic link needs Developer Mode or elevation, so these cases cannot run everywhere. The
/// idiom that matters is <see cref="Xunit.Assert.Skip(string)"/> rather than a bare <c>return</c>: a silent
/// return is indistinguishable from a pass, so nothing would tell anyone whether the case had ever run on
/// any machine. A reported skip lands in the run summary's <c>skipped:</c> count, which is what makes
/// "it runs in CI" a measured fact — 3 skipped on a developer box, 0 on the GitHub runner.
/// <para>Shared rather than copied per test class, for the same reason the walk it exercises is shared: four
/// copies of a rule is four chances for one of them to drift. See <c>SafeFileWalk</c>.</para>
/// </remarks>
internal static class Symlinks
{
    /// <summary>
    /// Links <paramref name="link"/> to the directory <paramref name="target"/>, or skips the calling test.
    /// Runs <paramref name="cleanup"/> before skipping, so a fixture built for the case is not left behind.
    /// </summary>
    internal static void RequireDirectoryLink(string link, string target, Action cleanup)
    {
        try { Directory.CreateSymbolicLink(link, target); return; }
        catch (IOException) { /* no privilege — fall through to the skip */ }
        catch (UnauthorizedAccessException) { /* no privilege — fall through to the skip */ }

        cleanup();
        Assert.Skip(Unavailable);
    }

    /// <summary>
    /// Links <paramref name="link"/> to the file <paramref name="target"/>, or skips the calling test.
    /// </summary>
    internal static void RequireFileLink(string link, string target, Action cleanup)
    {
        try { File.CreateSymbolicLink(link, target); return; }
        catch (IOException) { /* no privilege — fall through to the skip */ }
        catch (UnauthorizedAccessException) { /* no privilege — fall through to the skip */ }

        cleanup();
        Assert.Skip(Unavailable);
    }

    /// <summary>
    /// Deletes a directory tree that may contain links, without following them. The link goes first and
    /// non-recursively: a recursive delete through a symlink would remove the TARGET's contents, which in
    /// these fixtures is the data the test is proving does not get touched.
    /// </summary>
    internal static void RemoveLinkThenTree(string link, string root)
    {
        // Both shapes, because the caller may have made either kind of link and the fixture should not have
        // to remember which. Whichever one does not apply simply fails and is swallowed.
        Attempt(() => Directory.Delete(link, recursive: false));
        Attempt(() => File.Delete(link));
        Attempt(() => Directory.Delete(root, recursive: true));
    }

    /// <summary>Runs a teardown step, swallowing the faults a best-effort cleanup is expected to hit.</summary>
    private static void Attempt(Action step)
    {
        try { step(); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private const string Unavailable =
        "creating a symbolic link needs Developer Mode or elevation, and this machine has neither";
}
