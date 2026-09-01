// SysManager · UpdateApplier — in-process self-update applier
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Serilog;
using SysManager.Helpers;

[assembly: InternalsVisibleTo("SysManager.Tests")]

namespace SysManager.Services;

/// <summary>
/// Applies a downloaded update from inside the app itself, replacing the old
/// external <c>.cmd</c> updater script. When the user confirms Install, the
/// running app launches the freshly-downloaded (and already hash- and
/// signature-verified) executable with <see cref="ApplyUpdateArg"/>; that new
/// process intercepts the argument at startup, waits for the old process to
/// exit, swaps itself over the old executable via an atomic move, relaunches
/// the updated app, and exits.
///
/// This removes the previous design's writable <c>.cmd</c> file on disk and the
/// external <c>cmd.exe</c> invocation — there is no script for a same-user
/// process to tamper with between write and execution. The copy is staged to a
/// sibling temp file and moved into place, so an interrupted copy can never
/// leave a half-written (unlaunchable) executable at the target path.
/// </summary>
internal static class UpdateApplier
{
    /// <summary>Command-line sentinel that puts a started process into applier mode.</summary>
    public const string ApplyUpdateArg = "--apply-update";

    /// <summary>File name of the retained previous generation. One only, never a history.</summary>
    internal const string PreviousBuildFileName = "SysManager-previous.exe";

    /// <summary>
    /// Where the outgoing executable is kept so a bad update can be undone.
    /// </summary>
    /// <remarks>
    /// Lives in the existing <c>%LocalAppData%\SysManager\updates</c> folder rather than beside the
    /// portable .exe, so the "single portable file" identity is preserved — a user who copies the
    /// app to a USB stick still has exactly one file.
    /// </remarks>
    internal static string PreviousBuildPath(string? updatesDir = null) => Path.Join(
        updatesDir ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysManager", "updates"),
        PreviousBuildFileName);

    /// <summary>
    /// Where the SHA-256 of the retained previous build is recorded, so rollback can prove the binary
    /// it is about to launch is the one this app wrote.
    /// </summary>
    /// <remarks>
    /// SEC: the retained build lives in a user-writable directory and persists indefinitely between
    /// being written and the user clicking "Go back". Provenance is not integrity — any process running
    /// as the user can replace it, and rollback launches it with <c>UseShellExecute</c>, inheriting
    /// SysManager's token. The install path solves this with a hash check against the published
    /// <c>.sha256</c>; rollback has no published hash to compare with, so the applier records one at
    /// the moment it makes the copy.
    /// <para>The sidecar is no more tamper-proof than the binary — an attacker who can write one can
    /// write both. It is not meant to stop a determined local attacker; it removes the SILENT case,
    /// where a binary swapped by anything else (a half-finished copy, an unrelated tool, malware that
    /// does not know SysManager) is executed with no check at all.</para>
    /// </remarks>
    internal static string PreviousBuildHashPath(string? updatesDir = null) =>
        PreviousBuildPath(updatesDir) + ".sha256";

    /// <summary>
    /// Builds the command-line arguments handed to the downloaded executable so
    /// it applies itself over <paramref name="targetExe"/> after process
    /// <paramref name="pid"/> exits. A Windows path cannot legally contain a
    /// double quote, so a quote here means tampering/injection — reject rather
    /// than emit an argument string that could be mis-parsed.
    /// </summary>
    public static string BuildArguments(string targetExe, int pid)
    {
        if (targetExe.Contains('"'))
            throw new InvalidOperationException("Update target path contains an invalid character.");
        return $"{ApplyUpdateArg} \"{targetExe}\" {pid}";
    }

    /// <summary>
    /// Recognises applier mode from a process's command-line arguments. Returns
    /// true and the parsed target/pid when <paramref name="args"/> begins with
    /// <see cref="ApplyUpdateArg"/> followed by a target path and a numeric pid.
    /// The OS has already removed the surrounding quotes from the path argument.
    /// </summary>
    public static bool TryParseArgs(string[] args, out string targetExe, out int pid)
    {
        targetExe = string.Empty;
        pid = 0;
        if (args is null || args.Length < 3) return false;
        if (!string.Equals(args[0], ApplyUpdateArg, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(args[1])) return false;
        if (!int.TryParse(args[2], out pid) || pid <= 0) return false;
        targetExe = args[1];
        return true;
    }

    /// <summary>
    /// Decides whether <paramref name="targetExe"/> is a path the applier may overwrite.
    /// </summary>
    /// <remarks>
    /// SEC: this is the ONLY thing standing between <c>--apply-update</c> and an arbitrary-file
    /// overwrite. The applier branch in <c>App.OnStartup</c> runs before the single-instance mutex, DI
    /// and the CLI guard, so its target path arrives straight from <c>argv</c> — from whoever started
    /// the process, not necessarily from <see cref="BuildArguments"/>. The quote check in
    /// BuildArguments constrains only the string this app EMITS; an attacker types the command line
    /// directly, so the inbound side has to re-establish trust on its own.
    /// <para>Without this, <c>SysManager.exe --apply-update "&lt;any writable path&gt;" &lt;pid&gt;</c> copies this
    /// 85 MB executable over that path and then launches it — and because the app's documented
    /// workflow is "Run as administrator", the writable set can be every file on the machine.</para>
    /// <para>The rule is deliberately narrow: an update replaces SysManager with SysManager, so the
    /// target must be an EXISTING file (never a creation) carrying the same product resource as this
    /// executable. Requiring existence matters as much as the identity: a first install has nothing to
    /// update, and it stops the applier being used to plant a new file where none was. Rejecting a
    /// target under a system root is belt-and-braces for the case where someone has legitimately put a
    /// SysManager build inside Windows.</para>
    /// <para>The identity is the PRODUCT resource, never the file name. The applier is the freshly
    /// downloaded <c>SysManager-&lt;newVersion&gt;.exe</c> and the target is the older build it
    /// replaces, so a name-equality rule can never be satisfied by a real update — it refused every
    /// one, logged the refusal at Error, and returned after the old process had already exited.</para>
    /// <para>Returns a reason rather than a bare bool so the refusal can be logged specifically —
    /// a silent no-op here would look identical to a successful update.</para>
    /// </remarks>
    internal static bool IsValidApplyTarget(string targetExe, out string reason)
    {
        reason = "";

        if (string.IsNullOrWhiteSpace(targetExe))
        {
            reason = "the target path is empty";
            return false;
        }

        string full;
        try
        {
            // Canonicalise first: validate and act on the SAME string, so ".." segments or a relative
            // path resolved against the current directory cannot mean one thing here and another at
            // File.Move. Mirrors how UninstallerService validates fullPath then launches fullPath.
            full = Path.GetFullPath(targetExe);
        }
        catch (ArgumentException)
        {
            reason = "the target path is not a valid path";
            return false;
        }
        catch (NotSupportedException)
        {
            reason = "the target path is not a valid path";
            return false;
        }
        catch (PathTooLongException)
        {
            reason = "the target path is too long";
            return false;
        }

        // Location before existence: whether a path is protected is a property of the path, not of
        // whether a file happens to be sitting there. Checking it first also keeps this branch
        // deterministically reachable instead of being shadowed by the existence check below.
        if (IsUnderSystemRoot(full))
        {
            reason = "the target is inside a protected system directory";
            return false;
        }

        if (!File.Exists(full))
        {
            // An update REPLACES an install; it never creates one. This is what stops the applier
            // being aimed at a path that does not exist yet.
            reason = "the target does not exist, and an update only ever replaces an existing build";
            return false;
        }

        // "An update replaces SysManager with SysManager" — verified on the product resource, which
        // needs the file to exist, hence last. Compared against THIS executable's own product rather
        // than a hardcoded literal, keeping the original intent that a rename of what we ship cannot
        // silently disable the check.
        if (!TryReadProductName(Environment.ProcessPath, out var ownProduct))
        {
            // Fail closed: unable to establish what we are, so unable to establish what the target is.
            reason = "this build's product name could not be read, so the target cannot be verified";
            return false;
        }

        if (!TryReadProductName(full, out var targetProduct) ||
            !string.Equals(targetProduct, ownProduct, StringComparison.Ordinal))
        {
            reason = $"the target is not a {ownProduct} build";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads a file's Win32 <c>ProductName</c>, trimmed. False when there is none or it cannot be read.
    /// </summary>
    /// <remarks>
    /// The product resource, not the file NAME, is what identifies one of our builds. The name cannot
    /// do the job: the applier is the freshly downloaded <c>SysManager-&lt;newVersion&gt;.exe</c> while
    /// its target is the build being replaced, so requiring the two to match refused every real update.
    /// The product string is identical across versions and survives a user renaming the portable file,
    /// which is a documented thing to do with a single-file app.
    /// </remarks>
    private static bool TryReadProductName(string? path, out string product)
    {
        product = "";
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            product = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim() ?? "";
            return product.Length > 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> sits under the Windows or Program Files roots. Compared
    /// on a directory boundary so a sibling like <c>C:\WindowsApps</c> is not mistaken for
    /// <c>C:\Windows</c> — the same boundary rule FileShredderService and UninstallerService use.
    /// </summary>
    private static bool IsUnderSystemRoot(string fullPath)
    {
        foreach (var folder in (Environment.SpecialFolder[])
                 [
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.System,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 ])
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root)) continue;

            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Stages <paramref name="sourceExe"/> over <paramref name="targetExe"/> via
    /// a sibling <c>.new</c> temp file and an atomic move, retrying briefly while
    /// the target is still locked (the old process or an AV scanner may hold it
    /// for a moment after exit). The move is the only step that touches the live
    /// target path, so a failure mid-copy leaves the existing executable intact.
    /// Returns true on success.
    /// </summary>
    /// <param name="updatesDir">
    /// Where the retained previous generation is written; null resolves the real profile. This
    /// parameter exists because omitting it is not a harmless default in a test: the retained copy
    /// went to the caller's own <c>%LocalAppData%\SysManager\updates</c> and overwrote their genuine
    /// rollback build with a temp fixture. Redirecting <paramref name="targetExe"/> alone is not
    /// enough — see the ratchet note in ArchitectureTests and issue #1772.
    /// </param>
    internal static bool ApplyCopy(
        string sourceExe, string targetExe, int maxAttempts = 10, int delayMs = 500, string? updatesDir = null)
    {
        // A missing source is non-recoverable — without this guard File.Copy throws
        // FileNotFoundException (an IOException subtype), which the retry block below
        // would misread as a transient lock and burn the full backoff before failing.
        if (!File.Exists(sourceExe))
        {
            Log.Error("Update apply: source executable not found at {Source}", LogService.SanitizePath(sourceExe));
            return false;
        }

        var staging = targetExe + ".new";
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Copy(sourceExe, staging, overwrite: true);
                // Keep the outgoing build BEFORE the move destroys it. The move is what makes an
                // interrupted copy safe, but it also means a SUCCESSFUL update into a broken build
                // leaves nothing to go back to — and this project has shipped two launch-blocking
                // regressions. Best-effort: failing to retain a copy must never abort an update that
                // is otherwise fine, so PreserveCurrentBuild swallows its own errors.
                PreserveCurrentBuild(targetExe, updatesDir);
                // File.Copy reports success once Windows has the bytes in memory. The move that follows
                // is a metadata change, so a power cut between the two can leave targetExe — the app's
                // own executable — present and zero-length, which means it will not start at all.
                // Deliberately NOT AtomicFile.SwapIntoPlace: that replaces via File.Replace, which
                // copies the outgoing file's attributes onto the replacement, and inheriting the old
                // build's zone identifier and creation time is not a change worth making here.
                AtomicFile.FlushOntoDevice(staging);
                File.Move(staging, targetExe, overwrite: true);
                return true;
            }
            catch (IOException ex)
            {
                Log.Debug(ex, "Update apply: target busy, attempt {Attempt}/{Max}", attempt, maxAttempts);
                TryDelete(staging);
                if (attempt < maxAttempts) Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning(ex, "Update apply: access denied writing {Target}", LogService.SanitizePath(targetExe));
                TryDelete(staging);
                return false;
            }
        }
        Log.Error("Update apply: gave up after {Max} attempts — {Target} stayed locked", maxAttempts, LogService.SanitizePath(targetExe));
        return false;
    }

    /// <summary>
    /// Copies the build currently at <paramref name="targetExe"/> aside as the one retained
    /// previous generation, so a successful-but-broken update can be undone.
    /// </summary>
    /// <remarks>
    /// BEST-EFFORT by design. Everything here is a nice-to-have compared with completing the update:
    /// if the disk is full or the folder is unwritable, the update must still proceed rather than
    /// fail because a safety net could not be stretched. That is why every failure is swallowed and
    /// logged at Debug/Warning instead of propagating.
    /// <para>Exactly ONE generation is kept: the copy overwrites any earlier one, so retention is
    /// "current + one previous" rather than an unbounded pile of full-size executables.</para>
    /// </remarks>
    internal static void PreserveCurrentBuild(string targetExe, string? updatesDir = null)
    {
        try
        {
            if (!File.Exists(targetExe)) return;   // first install — nothing to preserve

            var previous = PreviousBuildPath(updatesDir);
            var dir = Path.GetDirectoryName(previous);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.Copy(targetExe, previous, overwrite: true);

            // Record the hash of what we just wrote, so RollBackAsync can prove the binary it launches
            // is still that copy. Hashed from the RETAINED file rather than the source, so the recorded
            // value describes the bytes that actually landed on disk. Written after the copy: a hash
            // present without its binary is harmless (rollback checks the binary exists first), whereas
            // a binary present without its hash is what we must never leave behind.
            File.WriteAllText(previous + ".sha256", ComputeFileHash(previous));
            Log.Information("Update apply: retained the outgoing build for rollback");
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Update apply: could not retain the previous build — rollback will be unavailable");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Update apply: access denied retaining the previous build — rollback will be unavailable");
        }
    }

    /// <summary>SHA-256 of a file as an uppercase hex string, matching the published .sha256 format.</summary>
    internal static string ComputeFileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Verifies the retained previous build against the hash recorded when it was written, returning
    /// the open read handle on success so the caller can launch the very bytes that were verified.
    /// </summary>
    /// <remarks>
    /// SEC: returns the HANDLE, not a bool. Verifying by path and then launching by path is two
    /// independent opens of a mutable file in a user-writable directory — the exact TOCTOU the install
    /// path closes by holding a <c>FileShare.Read</c> handle across <c>Process.Start</c>
    /// (see the comment in <c>AboutViewModel.InstallUpdateAsync</c>). Handing the handle back makes the
    /// caller inherit that property instead of re-opening.
    /// <para>Fails CLOSED: a missing or unreadable hash sidecar means "cannot prove this", which is a
    /// refusal, not a skip — the same decision <c>UpdateService</c> makes for a missing published
    /// <c>.sha256</c>.</para>
    /// </remarks>
    internal static bool TryOpenVerifiedPreviousBuild(
        string? updatesDir, out FileStream? verifiedStream, out string reason)
    {
        verifiedStream = null;
        reason = "";

        var previous = PreviousBuildPath(updatesDir);
        var hashFile = PreviousBuildHashPath(updatesDir);

        FileStream? stream = null;
        // Ownership transfers to the caller ONLY on the success path. Every other exit — an early return
        // OR an exception of any type — must close the handle here, and a per-branch stream.Dispose()
        // could not promise that: File.ReadAllText and SHA256.HashData can throw types outside the two
        // catches below, and that path leaked the handle (CodeQL cs/dispose-not-called-on-throw).
        //
        // A leaked deny-write handle here is worse than an ordinary handle leak: nothing could replace
        // SysManager-previous.exe until the process exited, so the NEXT legitimate update would fail to
        // refresh the rollback copy and quietly leave the user with a stale one. A single finally keyed on
        // whether ownership moved covers every exit, including ones not yet imagined.
        try
        {
            // Open with deny-write BEFORE hashing, and keep it open on the way out: from this point the
            // bytes cannot change under us.
            stream = new FileStream(previous, FileMode.Open, FileAccess.Read, FileShare.Read);

            if (!File.Exists(hashFile))
            {
                reason = "there is no recorded checksum for the saved version";
                return false;
            }

            var expected = File.ReadAllText(hashFile).Trim();
            if (expected.Length != 64)
            {
                reason = "the recorded checksum for the saved version is not readable";
                return false;
            }

            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning(
                    "Rollback: retained build hash mismatch — expected {Expected}, got {Actual}",
                    expected, actual);
                reason = "the saved version has changed since it was saved";
                return false;
            }

            verifiedStream = stream;
            return true;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Rollback: could not read the retained build to verify it");
            reason = "the saved version could not be read";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Rollback: access denied reading the retained build to verify it");
            reason = "the saved version could not be read";
            return false;
        }
        finally
        {
            // verifiedStream is non-null only where the caller took ownership; anywhere else the handle
            // is still ours to close.
            if (verifiedStream is null) stream?.Dispose();
        }
    }

    /// <summary>
    /// Runs the full applier sequence on the current (downloaded) process: wait
    /// for the old process to exit, swap this executable over the old one, then
    /// launch the updated app. If the swap fails the original executable is left
    /// untouched and relaunched, so a failed update can never brick the install.
    /// </summary>
    public static void Run(string targetExe, int oldPid)
    {
        // SEC: the gate sits here rather than in TryParseArgs so it cannot be bypassed by a future
        // second caller — Run is the only thing that writes, so Run is what must refuse.
        if (!IsValidApplyTarget(targetExe, out var reason))
        {
            Log.Error(
                "Update apply: refusing to write {Target} — {Reason}. An update may only replace this " +
                "application's own executable.",
                LogService.SanitizePath(targetExe), reason);
            return;
        }

        var sourceExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(sourceExe))
        {
            Log.Error("Update apply: cannot determine source executable path");
            return;
        }

        Log.Information("Update apply: waiting for old process {Pid} to exit", oldPid);
        WaitForProcessExit(oldPid, TimeSpan.FromSeconds(30));

        var applied = ApplyCopy(sourceExe, targetExe);
        Log.Information(applied
            ? "Update apply: swapped new build into place, relaunching"
            : "Update apply: copy failed, relaunching existing build unchanged");

        // Relaunch whatever is at the target path. On success it's the new
        // build; on failure it's the original — either way the user gets a
        // working app rather than a dead one. UseShellExecute lets the relaunch
        // inherit the applier's elevation, preserving the user's run-as-admin
        // state across the update.
        try
        {
            Process.Start(new ProcessStartInfo(targetExe) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Error(ex, "Update apply: failed to relaunch {Target}", LogService.SanitizePath(targetExe));
        }
    }

    private static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // No process with that id — it already exited. Nothing to wait for.
        }
        catch (InvalidOperationException)
        {
            // Process exited between lookup and wait.
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best-effort cleanup of the staging file */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }
}
