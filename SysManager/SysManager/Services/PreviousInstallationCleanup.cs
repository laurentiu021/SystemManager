// SysManager · PreviousInstallationCleanup
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Security;
using Microsoft.Win32;

namespace SysManager.Services;

/// <summary>
/// Removes the previous Windows installation, <c>Windows.old</c>, through Windows' own Disk Cleanup handler for it.
/// </summary>
/// <remarks>
/// Behind a seam because running it for real from a test would remove the previous installation of the PC running the
/// tests. Deep Cleanup reaches it for its Windows.old bucket (#2720).
/// </remarks>
public interface IPreviousInstallationCleanup
{
    /// <summary>Runs Windows' cleanup of the previous installation, and returns Disk Cleanup's exit code.</summary>
    /// <exception cref="SecurityException">SysManager is not running as administrator.</exception>
    /// <exception cref="UnauthorizedAccessException">SysManager is not running as administrator.</exception>
    /// <exception cref="InvalidOperationException">This Windows has no such cleanup, or no run of it could be set up.</exception>
    Task<int> RemoveAsync(CancellationToken ct);
}

/// <summary>
/// Windows' "Previous Installations" Disk Cleanup handler, run on its own through <c>cleanmgr.exe /sagerun</c>, the way
/// Disk Cleanup and Storage Sense remove a previous installation.
/// </summary>
/// <remarks>
/// Windows Setup moves the old Windows folder into <c>Windows.old</c> with its permissions, so most of it is owned by
/// TrustedInstaller, and an administrator may only read it. Deleted file by file, as Deep Cleanup did, most of it was
/// refused, and what went was enough to leave a previous installation that "Go back" in Settings can no longer use
/// (#2720). The handler removes all of it.
/// <para>A <c>/sagerun:N</c> run cleans every handler with its <c>StateFlagsNNNN</c> value set, so the run is given a
/// number no handler has a value for, the value is set on this handler alone, and it is taken off again whatever the run
/// did. A number the user's own <c>/sageset</c> uses is never touched.</para>
/// </remarks>
public sealed class PreviousInstallationCleanup : IPreviousInstallationCleanup
{
    /// <summary>Where Windows lists its Disk Cleanup handlers, under HKLM.</summary>
    internal const string VolumeCachesPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    /// <summary>The handler that removes a previous installation.</summary>
    internal const string HandlerName = "Previous Installations";

    /// <summary>The first of the run numbers SysManager picks from.</summary>
    internal const int FirstRun = 6400;

    /// <summary>The last of the run numbers SysManager picks from.</summary>
    internal const int LastRun = 6499;

    // What a StateFlags value tells /sagerun: clean this handler.
    private const int Selected = 2;

    private readonly IPowerShellRunner _ps;
    private readonly RegistryKey _baseKey;

    /// <summary>The handler on this PC, run through <paramref name="ps"/>.</summary>
    public PreviousInstallationCleanup(IPowerShellRunner ps) : this(ps, Registry.LocalMachine) { }

    /// <summary>The handler under <paramref name="baseKey"/>, so a test points it at a key of its own.</summary>
    internal PreviousInstallationCleanup(IPowerShellRunner ps, RegistryKey baseKey)
    {
        _ps = ps ?? throw new ArgumentNullException(nameof(ps));
        _baseKey = baseKey ?? throw new ArgumentNullException(nameof(baseKey));
    }

    public async Task<int> RemoveAsync(CancellationToken ct)
    {
        using var handlers = _baseKey.OpenSubKey(VolumeCachesPath)
            ?? throw new InvalidOperationException("Windows' Disk Cleanup is not on this PC.");
        var run = FreeRun(handlers)
            ?? throw new InvalidOperationException("Windows' Disk Cleanup has no run number free for it.");
        // Opened for writing only here, so a session without administrator rights is refused before anything is set.
        using var handler = handlers.OpenSubKey(HandlerName, writable: true)
            ?? throw new InvalidOperationException("Windows' cleanup of a previous installation is not on this PC.");

        var flag = StateFlag(run);
        handler.SetValue(flag, Selected, RegistryValueKind.DWord);
        try
        {
            // A bare name, which the runner resolves under System32 as it does every tool it starts.
            return await _ps.RunProcessAsync(
                "cleanmgr.exe", string.Create(CultureInfo.InvariantCulture, $"/sagerun:{run}"), ct,
                PowerShellRunner.OemEncoding).ConfigureAwait(false);
        }
        finally
        {
            handler.DeleteValue(flag, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// The first of SysManager's run numbers that no handler under <paramref name="handlers"/> has a value for, or null
    /// when every one is taken.
    /// </summary>
    internal static int? FreeRun(RegistryKey handlers)
    {
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        foreach (var name in handlers.GetSubKeyNames())
        {
            using var handler = handlers.OpenSubKey(name);
            if (handler is null) continue;
            foreach (var value in handler.GetValueNames())
                taken.Add(value);
        }

        for (var run = FirstRun; run <= LastRun; run++)
        {
            if (!taken.Contains(StateFlag(run))) return run;
        }
        return null;
    }

    /// <summary>The name of the value that selects a handler for run <paramref name="run"/>.</summary>
    internal static string StateFlag(int run) => string.Create(CultureInfo.InvariantCulture, $"StateFlags{run:D4}");
}
