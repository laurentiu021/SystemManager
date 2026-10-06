// SysManager · ISessionRestorePoint
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>
/// One System Restore point per app session, shared by every tab that changes system state, so the
/// safety net does not depend on which door the user came through.
/// <para>Before this existed, the Tweaks Hub tab (since merged into Privacy &amp; Telemetry) owned a private copy
/// of the logic and was the only place that had it: flipping a privacy toggle there took a snapshot, while the
/// identical registry write from the Privacy tab, or removing Edge, took none. An unpredictable
/// guarantee is the worst kind — it is the reason the user feels able to press the button at all.</para>
/// <para><b>Deliberately best-effort.</b> System Restore is off by default on much of consumer
/// Windows, needs administrator, and Windows rate-limits creation to roughly one point per 24 hours.
/// A "no" is therefore normal and must never be presented as protection that exists. Callers report
/// the snapshot ONLY when one was actually created.</para>
/// </summary>
public interface ISessionRestorePoint
{
    /// <summary>
    /// Attempts a restore point the first time it is called in this app session, and does nothing on
    /// every later call — one attempt per session, not one per click, because Windows would refuse the
    /// rest anyway and each attempt costs seconds.
    /// </summary>
    /// <param name="description">What the user will see in the System Restore list.</param>
    /// <returns>
    /// <c>true</c> only when THIS call created the point. A later caller in the same session gets
    /// <c>false</c> even though a point exists — ask <see cref="CreatedThisSession"/> for that. The
    /// split keeps "we just took a snapshot for you" honest and separate from "this session has one".
    /// </returns>
    Task<bool> EnsureAsync(string description, CancellationToken ct = default);

    /// <summary>
    /// Whether a restore point was actually created at any point in this session, by any caller.
    /// Never true when the attempt failed or was skipped.
    /// </summary>
    bool CreatedThisSession { get; }

    /// <summary>
    /// What a confirmation adds before a change that goes through <see cref="EnsureAsync"/>, or an empty string
    /// when that change will make no attempt. When not empty it starts with a blank line, so a caller appends it
    /// to its message as it is.
    /// </summary>
    /// <remarks>
    /// The attempt turns System Protection back on when it is off, a lasting change, and the seven tabs that
    /// reach this seam used to make it without saying so, while the Restore Points tab and Performance Mode
    /// both said so (#2483). Empty once this session's one attempt has been made, and when SysManager is not
    /// running as administrator, because neither the point nor the protection change can happen then.
    /// </remarks>
    string ConfirmationNotice { get; }
}
