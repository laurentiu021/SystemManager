// SysManager · Symlinks — one place that knows how a test asks for a link
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
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
/// <para>All four shapes live here, including the two built by <c>cmd /c mklink</c>, because the copies of
/// that invocation had already drifted apart: five of them across three files, each with its own verification
/// step, four private <c>IsReparse</c> helpers between them, and every one ignoring what
/// <see cref="Process.WaitForExit(int)"/> returned — so a timed-out <c>mklink</c> read
/// <see cref="Process.ExitCode"/> on a live process and threw <see cref="InvalidOperationException"/> instead
/// of skipping. <see cref="ArchitectureTests.EveryLinkATestNeeds_IsAskedForInOnePlace"/> keeps them here.</para>
/// </remarks>
internal static class Symlinks
{
    /// <summary>
    /// Links <paramref name="link"/> to the directory <paramref name="target"/>, or skips the calling test.
    /// Runs <paramref name="cleanup"/> before skipping, so a fixture built for the case is not left behind;
    /// pass nothing when the call already sits inside a <c>try</c> whose <c>finally</c> tears the fixture down,
    /// since the skip throws and that <c>finally</c> therefore runs anyway.
    /// </summary>
    internal static void RequireDirectoryLink(string link, string target, Action? cleanup = null)
    {
        EnsureParent(link);
        try { Directory.CreateSymbolicLink(link, target); return; }
        catch (IOException) { /* no privilege — fall through to the skip */ }
        catch (UnauthorizedAccessException) { /* no privilege — fall through to the skip */ }

        Skip(Unavailable, cleanup);
    }

    /// <summary>
    /// Links <paramref name="link"/> to the file <paramref name="target"/>, or skips the calling test.
    /// </summary>
    internal static void RequireFileLink(string link, string target, Action? cleanup = null)
    {
        EnsureParent(link);
        try { File.CreateSymbolicLink(link, target); return; }
        catch (IOException) { /* no privilege — fall through to the skip */ }
        catch (UnauthorizedAccessException) { /* no privilege — fall through to the skip */ }

        Skip(Unavailable, cleanup);
    }

    /// <summary>
    /// Points <paramref name="link"/> at the directory <paramref name="target"/> with an NTFS junction, or
    /// skips the calling test. A junction needs no privilege at all, which is exactly why the guards these
    /// tests cover exist: any standard user can plant one inside a folder the app is about to clean.
    /// </summary>
    internal static void RequireJunction(string link, string target, Action? cleanup = null)
    {
        // Verified by the reparse attribute rather than by existence: mklink can report success for a
        // directory that is not actually a reparse point, and a plain directory would let the test "pass"
        // while exercising none of the link handling it exists to prove.
        if (Mklink("/J", link, target, out var detail) && IsReparsePoint(link)) return;

        Skip($"this machine could not create a directory junction{detail}", cleanup);
    }

    /// <summary>
    /// Gives the file <paramref name="target"/> a second name at <paramref name="link"/>, or skips the
    /// calling test. Needs the same volume and NTFS; no privilege.
    /// </summary>
    internal static void RequireHardLink(string link, string target, Action? cleanup = null)
    {
        if (Mklink("/H", link, target, out var detail) && File.Exists(link)) return;

        Skip($"this machine could not create a hard link{detail}", cleanup);
    }

    /// <summary>True when <paramref name="path"/> is a junction or symbolic link rather than the real thing.</summary>
    internal static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Runs <c>cmd /c mklink &lt;flag&gt;</c> and reports whether it succeeded, with whatever it complained
    /// about in <paramref name="detail"/> so a skip says why instead of only that it happened.
    /// </summary>
    private static bool Mklink(string flag, string link, string target, out string detail)
    {
        EnsureParent(link);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink {flag} \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            detail = " (the shell did not start)";
            return false;
        }

        // The return value is checked, unlike in every copy this replaced: on a timeout the process is still
        // running and reading ExitCode throws InvalidOperationException, which reaches the test as an error
        // about the wrong thing entirely.
        if (!proc.WaitForExit(10_000))
        {
            detail = " (mklink did not finish within 10 seconds)";
            return false;
        }

        // Read after exit, so neither pipe can fill while the child waits for someone to drain it. mklink
        // writes one short line either way.
        var said = Collapse(proc.StandardError.ReadToEnd() + " " + proc.StandardOutput.ReadToEnd());
        detail = said.Length == 0 ? string.Empty : $" ({said})";
        return proc.ExitCode == 0;
    }

    /// <summary>
    /// Creates the link's parent directory. Two call sites rely on it, and without it a missing parent makes
    /// <see cref="Directory.CreateSymbolicLink"/> throw <see cref="DirectoryNotFoundException"/> — which
    /// derives from <see cref="IOException"/>, so a fixture bug would be reported as "no privilege".
    /// </summary>
    private static void EnsureParent(string link)
    {
        var parent = Path.GetDirectoryName(link);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    }

    private static void Skip(string reason, Action? cleanup)
    {
        cleanup?.Invoke();
        Assert.Skip(reason);
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
        RemoveTree(root);
    }

    /// <summary>
    /// Deletes a tree that holds no link, best-effort. Exists so a fixture with a second tree — typically the
    /// out-of-scope data the test is proving survives — does not fall back to a bare <c>catch { }</c>.
    /// </summary>
    internal static void RemoveTree(string root) => Attempt(() => Directory.Delete(root, recursive: true));

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
