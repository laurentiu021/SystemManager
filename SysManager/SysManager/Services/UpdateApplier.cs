// SysManager · UpdateApplier — in-process self-update applier
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Serilog;

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
    /// Stages <paramref name="sourceExe"/> over <paramref name="targetExe"/> via
    /// a sibling <c>.new</c> temp file and an atomic move, retrying briefly while
    /// the target is still locked (the old process or an AV scanner may hold it
    /// for a moment after exit). The move is the only step that touches the live
    /// target path, so a failure mid-copy leaves the existing executable intact.
    /// Returns true on success.
    /// </summary>
    internal static bool ApplyCopy(string sourceExe, string targetExe, int maxAttempts = 10, int delayMs = 500)
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
    /// Runs the full applier sequence on the current (downloaded) process: wait
    /// for the old process to exit, swap this executable over the old one, then
    /// launch the updated app. If the swap fails the original executable is left
    /// untouched and relaunched, so a failed update can never brick the install.
    /// </summary>
    public static void Run(string targetExe, int oldPid)
    {
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
