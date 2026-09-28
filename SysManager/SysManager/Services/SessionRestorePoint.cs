// SysManager · SessionRestorePoint
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Serilog;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// The one implementation of <see cref="ISessionRestorePoint"/>. Registered as a singleton so
/// "once per session" means once for the whole app, not once per tab: two tabs each taking their own
/// snapshot would burn the 24-hour rate limit on a duplicate and leave the second user action
/// unprotected for no benefit.
/// </summary>
/// <remarks>
/// Takes the single capability it needs — "create a point, tell me whether it happened" — rather
/// than <see cref="RestorePointService"/> itself, which is sealed and therefore unmockable. That
/// would have forced either an interface across every existing consumer or unsealing production
/// code for the benefit of a test; a delegate keeps the seam where it belongs and lets these tests
/// run with no proxy framework at all.
/// </remarks>
public sealed class SessionRestorePoint(
    Func<string, CancellationToken, Task<bool>> createAsync, Func<bool>? isElevated = null)
    : ISessionRestorePoint
{
    // Interlocked rather than a plain bool: mutating commands live on the UI thread today, but the
    // Gaming Profile's auto-revert already runs from a thread-pool Process.Exited callback, so a
    // future caller on that path must not be able to slip a second attempt through the check.
    private int _attempted;
    private volatile bool _created;

    // Whether an attempt could change anything: creating a point, and turning System Protection on with it,
    // needs administrator. A test passes its own answer rather than depending on how its host was started.
    private readonly Func<bool> _isElevated = isElevated ?? AdminHelper.IsElevated;

    /// <inheritdoc />
    public bool CreatedThisSession => _created;

    /// <inheritdoc />
    public string ConfirmationNotice =>
        Volatile.Read(ref _attempted) == 0 && _isElevated()
            ? "\n\nSysManager will try to create a System Restore point first. " + RestorePointService.ProtectionNotice
            : "";

    /// <inheritdoc />
    public async Task<bool> EnsureAsync(string description, CancellationToken ct = default)
    {
        // Claim the single attempt BEFORE awaiting, so a slow or failing snapshot is not retried
        // ahead of every subsequent batch — the behaviour TweaksHubService established.
        if (Interlocked.Exchange(ref _attempted, 1) == 1) return false;

        try
        {
            // ConfigureAwait(true): callers are UI-thread commands whose post-await code mutates
            // bound state. RestorePointService.CreateAsync hops to the thread pool internally, so
            // without this the continuation would resume off the UI thread — a real cross-thread
            // defect, and the reason the original TweaksHub call was written this way.
            var created = await createAsync(description, ct).ConfigureAwait(true);
            _created = created;
            Log.Information("Session restore point for {Description}: created={Created}", description, created);
            return created;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            // Not an error path worth surfacing: no admin, System Restore disabled, or rate-limited.
            // The caller reports nothing, which is the honest outcome.
            Log.Debug("Session restore point skipped ({Description}): {Error}", description, ex.Message);
            return false;
        }
    }
}
