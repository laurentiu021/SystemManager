// SysManager · EdgeOneDriveService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>The honest outcome of an Edge/OneDrive operation, so the ViewModel can report
/// exactly what happened rather than guessing from an exception.</summary>
public enum EdgeOneDriveOutcome
{
    /// <summary>The operation completed.</summary>
    Success,
    /// <summary>A machine-scope change (Edge HKLM policy / scheduled tasks) was denied — needs administrator.</summary>
    NeedsAdmin,
    /// <summary>Nothing to do — the component isn't installed on this system.</summary>
    NotApplicable,
    /// <summary>The operation was attempted but did not succeed.</summary>
    Failed,
}

/// <summary>
/// Safely de-integrates Microsoft Edge and OneDrive, and restores them — the two most-requested
/// "get this off my machine" targets, handled without the brittle manual registry surgery users
/// otherwise copy from forums.
/// <para>
/// <b>OneDrive is fully removable (per-user, no elevation).</b> Removal stops the running client,
/// runs the official <c>OneDriveSetup.exe /uninstall</c>, and unpins the File Explorer navigation-
/// pane entry; every step is reversible (re-run the setup, re-pin). The uninstaller itself clears
/// OneDrive's startup entry and shell integration.
/// </para>
/// <para>
/// <b>Edge is NEVER uninstalled.</b> There is no supported way to remove Edge (it backs WebView2
/// and Windows Update reinstalls it), and forcing it breaks other apps irreversibly. Instead this
/// offers a fully-reversible "disable &amp; de-integrate": turn off Edge's background mode and
/// startup boost via the documented Group-Policy keys and disable its auto-update scheduled tasks.
/// Those are machine-scope (HKLM / machine tasks) and therefore need administrator; restore clears
/// the policy values and re-enables the tasks. Changing the default browser can't be done
/// programmatically on modern Windows (the UserChoice association is hash-protected), so the tab
/// guides the user to the Settings page rather than pretending to switch it.
/// </para>
/// <para>
/// All PowerShell/process work routes through the <see cref="IPowerShellRunner"/> seam and every
/// script is a hard-coded constant with no user input, so there is no injection surface. The
/// registry roots are injectable (mirroring <see cref="WindowsUpdatePolicyService"/> and
/// <see cref="AppBlockerService"/>) so the pin/policy logic is unit-tested against a redirected
/// HKCU subkey without administrator rights or touching real machine state.
/// </para>
/// </summary>
public sealed partial class EdgeOneDriveService
{
    private readonly IPowerShellRunner _ps;
    private readonly RegistryKey _hkcuRoot;   // OneDrive nav-pane pin (Software\Classes\CLSID\…)
    private readonly RegistryKey _hklmRoot;   // Edge policies (SOFTWARE\Policies\Microsoft\Edge)
    private readonly string? _oneDriveSetupPath;
    private readonly string? _oneDriveClientPath;
    private readonly Func<bool> _isOneDriveRunning;

    /// <summary>
    /// Creates the service. <paramref name="hkcuRoot"/> defaults to <see cref="Registry.CurrentUser"/>
    /// (OneDrive's per-user nav-pane pin) and <paramref name="hklmRoot"/> to
    /// <see cref="Registry.LocalMachine"/> (Edge's machine policy, needs admin). Tests pass redirected
    /// roots (HKCU subkeys) so the logic runs without elevation or real machine writes.
    /// The optional <paramref name="oneDriveSetupPath"/> override exists for testing, as
    /// <see cref="HostsFileService"/>'s does: it replaces the setup locations probed below, so the
    /// remove/restore outcome can be tested on a machine without OneDrive. The runner is substituted
    /// there, so the file is never launched. <paramref name="oneDriveClientPath"/> does the same for OneDrive.exe,
    /// whose presence is what "installed" means, and <paramref name="isOneDriveRunning"/> replaces the look at the
    /// running processes, so neither answer depends on the machine running the test.
    /// </summary>
    public EdgeOneDriveService(
        IPowerShellRunner ps, RegistryKey? hkcuRoot = null, RegistryKey? hklmRoot = null,
        string? oneDriveSetupPath = null, string? oneDriveClientPath = null, Func<bool>? isOneDriveRunning = null)
    {
        _ps = ps;
        _hkcuRoot = hkcuRoot ?? Registry.CurrentUser;
        _hklmRoot = hklmRoot ?? Registry.LocalMachine;
        _oneDriveSetupPath = oneDriveSetupPath;
        _oneDriveClientPath = oneDriveClientPath;
        _isOneDriveRunning = isOneDriveRunning ?? IsOneDriveRunning;
    }

    // OneDrive's File Explorer navigation-pane entry is a shell namespace CLSID; toggling
    // System.IsPinnedToNameSpaceTree (0/1) hides/shows it (both the native and Wow6432Node views).
    private const string OneDriveClsidPath = @"Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}";
    private const string OneDriveClsidWowPath = @"Software\Classes\Wow6432Node\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}";
    private const string PinValue = "System.IsPinnedToNameSpaceTree";

    // Documented Microsoft Edge enterprise-policy values. Setting both to 0 stops Edge from running
    // in the background after it's closed and from preloading at sign-in; deleting them restores the
    // Windows defaults. The key lives under HKLM, so writing needs administrator.
    private const string EdgePolicyPath = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string BackgroundModeValue = "BackgroundModeEnabled";
    private const string StartupBoostValue = "StartupBoostEnabled";

    /// <summary>
    /// What Edge says while it is disabled, and why. Shown in the Disable confirmation and on the tab, and quoted in
    /// the README, which a guard holds to this text.
    /// </summary>
    /// <remarks>
    /// The two values are machine policies, and Edge shows "Your browser is managed by your organization" whenever a
    /// machine policy is set. On a personal PC that is the best-known sign of an unwanted program having taken over
    /// the browser, and nothing said it came from here (#2880).
    /// </remarks>
    public const string ManagedBrowserNotice =
        "While Edge is disabled, it says \"" + ManagedBrowserMessage + "\" in its menu and settings. "
        + "That message comes from the two Edge policies SysManager sets, not from anything harmful. Restore Edge "
        + "removes them, and the message with them, unless something else on this PC sets Edge policies too.";

    /// <summary>The words Edge shows while a machine policy is set.</summary>
    public const string ManagedBrowserMessage = "Your browser is managed by your organization";

    // The ONLY scheduled tasks this service will ever touch — Edge's two machine auto-update tasks.
    // A fixed allowlist (never a user- or catalog-supplied name) means the disable/enable path can
    // never be pointed at an arbitrary task. Each name is validated as embedding-safe by a unit test.
    // Each is matched as a prefix in the root task folder: current Edge Update adds an ID to the name, as in
    // "MicrosoftEdgeUpdateTaskMachineCore{...}", so the exact name found no task, and Disable and Restore
    // changed none while reporting success (#2810).
    internal static readonly string[] EdgeUpdateTaskNames =
    [
        "MicrosoftEdgeUpdateTaskMachineCore",
        "MicrosoftEdgeUpdateTaskMachineUA",
    ];

    // Stops the running OneDrive client so the uninstaller isn't blocked by a live process.
    private const string KillOneDriveScript =
        "Stop-Process -Name OneDrive -Force -ErrorAction SilentlyContinue";

    /// <summary>
    /// Reads the current Edge/OneDrive integration state for the status panel. Never throws —
    /// unreadable signals fall back to their safe default (treated as "not de-integrated").
    /// </summary>
    public async Task<EdgeOneDriveStatus> GetStatusAsync(CancellationToken ct = default)
    {
        bool oneDriveInstalled = IsOneDriveClientInstalled();
        bool oneDriveRunning = _isOneDriveRunning();
        bool oneDrivePinned = ReadOneDrivePinned(oneDriveInstalled);
        bool edgeInstalled = ResolveEdgeExe() is not null;
        bool edgeBackgroundDisabled = ReadEdgeBackgroundDisabled();
        bool edgeUpdateTasksEnabled = await QueryEdgeUpdateTasksEnabledAsync(ct).ConfigureAwait(false);

        return new EdgeOneDriveStatus(
            OneDriveInstalled: oneDriveInstalled,
            OneDriveRunning: oneDriveRunning,
            OneDrivePinned: oneDrivePinned,
            EdgeInstalled: edgeInstalled,
            EdgeUpdateTasksEnabled: edgeUpdateTasksEnabled,
            EdgeBackgroundDisabled: edgeBackgroundDisabled);
    }

    // ── OneDrive (per-user, no elevation) ───────────────────────────────────

    /// <summary>
    /// Removes OneDrive for the current user: stops the client, runs the official uninstaller,
    /// and unpins the File Explorer navigation-pane entry. Reversible via
    /// <see cref="RestoreOneDriveAsync"/>. Returns <see cref="EdgeOneDriveOutcome.NotApplicable"/>
    /// if OneDrive isn't installed, and <see cref="EdgeOneDriveOutcome.Failed"/>, with the entry left as it was, when
    /// the uninstaller fails or cannot be found.
    /// </summary>
    public async Task<EdgeOneDriveOutcome> RemoveOneDriveAsync(CancellationToken ct = default)
    {
        if (!IsOneDriveClientInstalled()) return EdgeOneDriveOutcome.NotApplicable;
        var setup = ResolveOneDriveSetup();
        if (setup is null)
        {
            // Installed with nothing to uninstall it: "not installed" here would contradict the card.
            Log.Warning("OneDrive: installed, but no OneDriveSetup.exe was found to uninstall it");
            return EdgeOneDriveOutcome.Failed;
        }

        try { await _ps.RunAsync(KillOneDriveScript, cancellationToken: ct).ConfigureAwait(false); }
        catch (System.Management.Automation.RuntimeException ex) { Log.Debug("OneDrive: stop-process failed: {Error}", ex.Message); }

        int exit;
        try
        {
            exit = await _ps.RunProcessAsync(setup, "/uninstall", ct).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning("OneDrive: uninstall launch failed: {Error}", ex.Message);
            return EdgeOneDriveOutcome.Failed;
        }

        Log.Information("OneDrive: uninstall requested (exit {Exit})", exit);
        // Only a removal that worked takes the File Explorer entry away (#2810). It went whatever the exit code, so a
        // failed uninstall left an installed OneDrive missing from the sidebar. Restore pins only after success too.
        if (exit != 0) return EdgeOneDriveOutcome.Failed;
        // The uninstaller does not always clear the entry at once.
        SetOneDrivePinned(false);
        return EdgeOneDriveOutcome.Success;
    }

    /// <summary>
    /// Reinstalls OneDrive for the current user (re-runs the setup that survived under System32/
    /// SysWOW64) and, once it has succeeded, re-pins the File Explorer entry. Returns
    /// <see cref="EdgeOneDriveOutcome.NotApplicable"/> if no OneDrive setup can be found, and
    /// <see cref="EdgeOneDriveOutcome.Failed"/> when the setup exits non-zero, as removal does.
    /// </summary>
    public async Task<EdgeOneDriveOutcome> RestoreOneDriveAsync(CancellationToken ct = default)
    {
        var setup = ResolveOneDriveSetup();
        if (setup is null) return EdgeOneDriveOutcome.NotApplicable;

        int exit;
        try
        {
            exit = await _ps.RunProcessAsync(setup, string.Empty, ct).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning("OneDrive: reinstall launch failed: {Error}", ex.Message);
            return EdgeOneDriveOutcome.Failed;
        }

        Log.Information("OneDrive: reinstall requested (exit {Exit})", exit);
        // The exit code decides, as it does for removal (#2443). It used to be discarded, so a failed setup
        // reported "OneDrive restored." and pinned a File Explorer entry for a client that was not installed.
        if (exit != 0) return EdgeOneDriveOutcome.Failed;
        SetOneDrivePinned(true);
        return EdgeOneDriveOutcome.Success;
    }

    // ── Edge (machine-scope, needs admin; disable & de-integrate only) ───────

    /// <summary>
    /// Disables Edge's background mode and startup boost (HKLM Group-Policy keys) and disables its
    /// auto-update scheduled tasks. Fully reversible via <see cref="RestoreEdgeAsync"/>. Never
    /// uninstalls Edge. Returns <see cref="EdgeOneDriveOutcome.NeedsAdmin"/> if the machine policy
    /// write is denied, and <see cref="EdgeOneDriveOutcome.Failed"/>, with the policy values put back as they were,
    /// when no update task is found or one cannot be disabled.
    /// </summary>
    public async Task<EdgeOneDriveOutcome> DisableEdgeAsync(CancellationToken ct = default)
    {
        // Read first, so a change that does not complete leaves both values as it found them (#2810). Put back rather
        // than deleted: either may have been set before, by an earlier Disable or by whoever manages the PC.
        var before = ReadEdgePolicyValues();
        if (!TrySetEdgeBackgroundDisabled(true)) return EdgeOneDriveOutcome.NeedsAdmin;
        var done = false;
        try
        {
            done = await TrySetEdgeTasksAsync(enabled: false, ct).ConfigureAwait(false);
        }
        finally
        {
            // Also when the run throws rather than reports, as a cancelled one does.
            if (!done) RestoreEdgePolicyValues(before);
        }
        if (!done) return EdgeOneDriveOutcome.Failed;
        Log.Information("Edge: disabled background/startup-boost and update tasks");
        return EdgeOneDriveOutcome.Success;
    }

    /// <summary>
    /// Restores Edge to Windows defaults: clears the background/startup-boost policy values and
    /// re-enables its auto-update scheduled tasks. Returns <see cref="EdgeOneDriveOutcome.NeedsAdmin"/>
    /// if the machine policy write is denied, and <see cref="EdgeOneDriveOutcome.Failed"/> when no update task is
    /// found or one cannot be enabled.
    /// </summary>
    public async Task<EdgeOneDriveOutcome> RestoreEdgeAsync(CancellationToken ct = default)
    {
        if (!TrySetEdgeBackgroundDisabled(false)) return EdgeOneDriveOutcome.NeedsAdmin;
        if (!await TrySetEdgeTasksAsync(enabled: true, ct).ConfigureAwait(false)) return EdgeOneDriveOutcome.Failed;
        Log.Information("Edge: restored background/startup-boost and update tasks");
        return EdgeOneDriveOutcome.Success;
    }

    // ── Registry helpers (OneDrive pin) ─────────────────────────────────────

    private void SetOneDrivePinned(bool pinned)
    {
        foreach (var path in new[] { OneDriveClsidPath, OneDriveClsidWowPath })
        {
            try
            {
                using var key = _hkcuRoot.CreateSubKey(path, writable: true);
                key?.SetValue(PinValue, pinned ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (UnauthorizedAccessException ex) { Log.Debug("OneDrive pin write denied {Path}: {Error}", path, ex.Message); }
            catch (System.Security.SecurityException ex) { Log.Debug("OneDrive pin write denied {Path}: {Error}", path, ex.Message); }
            catch (IOException ex) { Log.Debug("OneDrive pin write I/O error {Path}: {Error}", path, ex.Message); }
        }
    }

    private bool ReadOneDrivePinned(bool installed)
    {
        foreach (var path in new[] { OneDriveClsidPath, OneDriveClsidWowPath })
        {
            try
            {
                using var key = _hkcuRoot.OpenSubKey(path);
                if (key?.GetValue(PinValue) is int v) return v != 0;
            }
            catch (System.Security.SecurityException) { /* protected key — try the other view */ }
            catch (IOException) { /* transient — try the other view */ }
        }
        // No explicit pin value present: OneDrive pins itself by default when installed.
        return installed;
    }

    // ── Registry helpers (Edge policy) ──────────────────────────────────────

    /// <summary>
    /// Writes (disabled=true) or clears (disabled=false) the Edge background/startup-boost policy
    /// values. Returns false when the HKLM write is denied (no elevation) so the caller can report
    /// NeedsAdmin — the same "write denied ⇒ no change" signal <see cref="WindowsUpdatePolicyService"/>
    /// uses.
    /// </summary>
    private bool TrySetEdgeBackgroundDisabled(bool disabled)
    {
        try
        {
            using var key = _hklmRoot.CreateSubKey(EdgePolicyPath, writable: true);
            if (key is null) return false;
            if (disabled)
            {
                key.SetValue(BackgroundModeValue, 0, RegistryValueKind.DWord);
                key.SetValue(StartupBoostValue, 0, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(BackgroundModeValue, throwOnMissingValue: false);
                key.DeleteValue(StartupBoostValue, throwOnMissingValue: false);
            }
            return true;
        }
        catch (UnauthorizedAccessException ex) { Log.Debug("Edge policy write denied: {Error}", ex.Message); return false; }
        catch (System.Security.SecurityException ex) { Log.Debug("Edge policy write denied: {Error}", ex.Message); return false; }
        catch (IOException ex) { Log.Debug("Edge policy write I/O error: {Error}", ex.Message); return false; }
    }

    private bool ReadEdgeBackgroundDisabled()
    {
        try
        {
            using var key = _hklmRoot.OpenSubKey(EdgePolicyPath);
            if (key is null) return false;
            return key.GetValue(BackgroundModeValue) is int bg && bg == 0
                && key.GetValue(StartupBoostValue) is int sb && sb == 0;
        }
        catch (System.Security.SecurityException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>One Edge policy value as it was: its data and registry type, or no data when it was not set.</summary>
    private readonly record struct EdgePolicyValue(object? Data, RegistryValueKind Kind);

    /// <summary>Both Edge policy values as they were before a change.</summary>
    private readonly record struct EdgePolicyValues(EdgePolicyValue BackgroundMode, EdgePolicyValue StartupBoost);

    /// <summary>
    /// Both policy values as they are now, for <see cref="RestoreEdgePolicyValues"/>. A key that cannot be read reads
    /// as both unset.
    /// </summary>
    private EdgePolicyValues ReadEdgePolicyValues()
    {
        try
        {
            using var key = _hklmRoot.OpenSubKey(EdgePolicyPath);
            return new EdgePolicyValues(Read(key, BackgroundModeValue), Read(key, StartupBoostValue));
        }
        catch (System.Security.SecurityException) { return default; }
        catch (IOException) { return default; }

        static EdgePolicyValue Read(RegistryKey? key, string name) =>
            key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } data
                ? new EdgePolicyValue(data, key.GetValueKind(name))
                : default;
    }

    /// <summary>
    /// Puts both policy values back as <see cref="ReadEdgePolicyValues"/> found them: written again with their own
    /// type, or deleted when they were not set.
    /// </summary>
    private void RestoreEdgePolicyValues(EdgePolicyValues before)
    {
        try
        {
            using var key = _hklmRoot.CreateSubKey(EdgePolicyPath, writable: true);
            if (key is null) return;
            Put(key, BackgroundModeValue, before.BackgroundMode);
            Put(key, StartupBoostValue, before.StartupBoost);
            Log.Information("Edge: the update tasks were not changed, so the policy values were put back");
        }
        catch (UnauthorizedAccessException ex) { Log.Warning("Edge policy not put back: {Error}", ex.Message); }
        catch (System.Security.SecurityException ex) { Log.Warning("Edge policy not put back: {Error}", ex.Message); }
        catch (IOException ex) { Log.Warning("Edge policy not put back: {Error}", ex.Message); }

        static void Put(RegistryKey key, string name, EdgePolicyValue value)
        {
            if (value.Data is null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, value.Data, value.Kind);
        }
    }

    // ── Scheduled-task helpers (Edge auto-update) ───────────────────────────

    /// <summary>
    /// The pipeline that finds Edge's update tasks: a name starting with one of the fixed prefixes, in the root task
    /// folder. A wildcard <c>-TaskName</c> that matches nothing returns nothing, without the error it raises beside a
    /// <c>-TaskPath</c>, so the folder is checked after.
    /// </summary>
    private static string FindEdgeTasksPipeline(string errorAction)
    {
        var prefixes = string.Join(",", EdgeUpdateTaskNames.Select(n => $"'{n}*'"));
        return $"@(Get-ScheduledTask -TaskName {prefixes} -ErrorAction {errorAction} " +
               "| Where-Object { $_.TaskPath -eq '\\' })";
    }

    /// <summary>
    /// Builds the hard-coded enable/disable script over the fixed Edge update-task allowlist. The
    /// task names are compile-time constants validated as embedding-safe (<see cref="IsSafeTaskName"/>),
    /// so this carries no injection surface even though the names are interpolated. It changes only the tasks not
    /// already as asked, puts back the ones it changed when one refuses, and reports how many tasks it found and
    /// whether one refused.
    /// </summary>
    internal static string BuildSetEdgeTasksScript(bool enabled)
    {
        var (verb, undo, alreadyDone) = enabled
            ? ("Enable-ScheduledTask", "Disable-ScheduledTask", "-ne")
            : ("Disable-ScheduledTask", "Enable-ScheduledTask", "-eq");
        return $"$tasks = {FindEdgeTasksPipeline("Stop")}; $changed = @(); $refused = $false; " +
               $"foreach ($t in $tasks) {{ if ([string]$t.State {alreadyDone} 'Disabled') {{ continue }}; " +
               $"try {{ {verb} -InputObject $t -ErrorAction Stop | Out-Null; $changed += $t }} " +
               "catch { $refused = $true; break } }; " +
               "if ($refused) { foreach ($t in $changed) { " +
               $"{undo} -InputObject $t -ErrorAction SilentlyContinue | Out-Null }} }}; " +
               "[PSCustomObject]@{ Found = $tasks.Count; Refused = $refused }";
    }

    internal static string BuildQueryEdgeTasksScript() =>
        $"$tasks = {FindEdgeTasksPipeline("SilentlyContinue")}; " +
        "[PSCustomObject]@{ AnyEnabled = (@($tasks | Where-Object { [string]$_.State -ne 'Disabled' }).Count -gt 0) }";

    /// <summary>
    /// Runs the enable/disable script and says whether it did its job: at least one update task found, and none
    /// refused. None found is not done, since the confirmation promises the tasks (#2810).
    /// </summary>
    private async Task<bool> TrySetEdgeTasksAsync(bool enabled, CancellationToken ct)
    {
        var change = enabled ? "enabled" : "disabled";
        try
        {
            var results = await _ps.RunAsync(BuildSetEdgeTasksScript(enabled), cancellationToken: ct)
                .ConfigureAwait(false);
            var report = results.Count > 0 ? results[0] : null;
            var found = report?.Properties["Found"]?.Value as int? ?? 0;
            var refused = report?.Properties["Refused"]?.Value is not false;
            if (found > 0 && !refused) return true;
            Log.Warning("Edge: update tasks not {Change}: {Found} found, one refused: {Refused}",
                change, found, refused);
            return false;
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Warning("Edge: update tasks could not be {Change}: {Error}", change, ex.Message);
            return false;
        }
    }

    private async Task<bool> QueryEdgeUpdateTasksEnabledAsync(CancellationToken ct)
    {
        try
        {
            var results = await _ps.RunAsync(BuildQueryEdgeTasksScript(), cancellationToken: ct).ConfigureAwait(false);
            if (results.Count == 0) return false;
            // The script projects a PowerShell [bool], which marshals back as System.Boolean.
            return results[0].Properties["AnyEnabled"]?.Value is true;
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Debug("Edge: query update tasks failed: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>A task name is safe to embed in a script only if it is purely alphanumeric —
    /// no quotes, whitespace, or PowerShell metacharacters. The Edge allowlist entries all pass;
    /// a unit test enforces it so a future edit can't introduce an unsafe name.</summary>
    internal static bool IsSafeTaskName(string name) => SafeTaskNameRegex().IsMatch(name);

    [GeneratedRegex(@"\A[A-Za-z0-9]+\z")]
    private static partial Regex SafeTaskNameRegex();

    // ── Filesystem/process probes ───────────────────────────────────────────

    /// <summary>
    /// Resolves the OneDriveSetup.exe to launch, preferring the trusted, admin-only-writable
    /// System32/SysWOW64 copy (which also survives an uninstall, so restore still works) and
    /// falling back to the per-user copy under LOCALAPPDATA. Returns null when none exists, which says
    /// nothing about whether OneDrive is installed. Preferring the System copy also avoids launching a user-writable
    /// binary should the app ever be elevated for the Edge portion (binary-planting guard, matching
    /// <see cref="SysManager.Helpers.SystemPaths"/>). A test override replaces the candidates; it is probed the same
    /// way.
    /// </summary>
    private string? ResolveOneDriveSetup()
    {
        if (_oneDriveSetupPath is not null)
            return File.Exists(_oneDriveSetupPath) ? _oneDriveSetupPath : null;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] candidates =
        [
            Path.Combine(windows, "SysWOW64", "OneDriveSetup.exe"),
            Path.Combine(windows, "System32", "OneDriveSetup.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "OneDrive", "OneDriveSetup.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// True when OneDrive itself is installed: its OneDrive.exe, under LOCALAPPDATA for one account or under
    /// Program Files for every account. Not the setup, which Windows keeps in System32 after an uninstall so
    /// OneDrive can be put back: decided by it, the card said "OneDrive is installed." right after a removal
    /// (#2810). A test override replaces the candidates.
    /// </summary>
    private bool IsOneDriveClientInstalled()
    {
        if (_oneDriveClientPath is not null) return File.Exists(_oneDriveClientPath);

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "OneDrive", "OneDrive.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft OneDrive", "OneDrive.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft OneDrive", "OneDrive.exe"),
        ];
        return candidates.Any(File.Exists);
    }

    private static string? ResolveEdgeExe()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsOneDriveRunning()
    {
        var procs = System.Diagnostics.Process.GetProcessesByName("OneDrive");
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }
}
