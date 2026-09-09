// SysManager · StartupEntry — model for startup items
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A single program that runs at Windows startup. Toggling IsEnabled
/// renames the registry value (adds/removes a "Disabled_" prefix) —
/// completely non-destructive and reversible.
/// </summary>
public sealed partial class StartupEntry : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _location = "";      // e.g. "HKCU\...\Run"
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _publisher = "";
    [ObservableProperty] private StartupSource _source;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private ImageSource? _icon;

    /// <summary>
    /// Plain-language description of the program from the built-in database
    /// (<see cref="Services.ProcessDescriptionService"/>), or empty when the program is not recognised.
    /// The tab used to show only the raw command line, which tells the target persona nothing about
    /// whether an entry is safe to turn off.
    /// </summary>
    [ObservableProperty] private string _description = "";

    /// <summary>
    /// Provenance from the built-in database as a <see cref="Services.ProcessSafety"/> name
    /// ("System" / "Trusted" / "Unknown"), or empty when the program is not recognised. A string, not
    /// the enum, so it binds to the same ProcessSafety* chip converters the Process Manager tab uses —
    /// and so an unrecognised entry renders NOTHING rather than defaulting to a colour it has not earned.
    /// </summary>
    [ObservableProperty] private string _safety = "";

    /// <summary>
    /// How long Windows measured this entry delaying the most recent start-up it noticed, formatted the way
    /// the Boot Analyzer tab formats it ("3.2 s", "800 ms"). Empty when Windows reported nothing about it.
    /// </summary>
    /// <remarks>
    /// Windows' own measurement, from the Diagnostics-Performance events the Boot Analyzer tab already reads
    /// — not an estimate this app invents. Empty is the normal case: Windows only records a component when it
    /// crossed a delay threshold, and reading those events at all requires administrator rights.
    /// <para>Blank rather than "0 s" or "None" when there is no measurement. The distinction matters on a
    /// column whose whole purpose is "which of these twenty is worth turning off": a zero would be a claim
    /// that this entry is fast, and nothing here supports that claim.</para>
    /// </remarks>
    [ObservableProperty] private string _startupImpact = "";

    /// <summary>
    /// The raw delay in milliseconds behind <see cref="StartupImpact"/>, or 0 when unmeasured. Exists so the
    /// column sorts numerically — sorting the display string would put "800 ms" above "3.2 s".
    /// </summary>
    [ObservableProperty] private long _startupImpactMs;

    /// <summary>
    /// The whole sentence behind <see cref="StartupImpact"/>, including when the start-up was and what
    /// Windows classified the component as. The column shows the bare figure; this goes on its tooltip.
    /// </summary>
    [ObservableProperty] private string _startupImpactDetail = "";

    /// <summary>Registry key path (for registry-based entries).</summary>
    public string RegistryKey { get; init; } = "";

    /// <summary>Registry value name (original, without Disabled_ prefix).</summary>
    public string ValueName { get; init; } = "";

    /// <summary>Task Scheduler path (for scheduled task entries).</summary>
    public string TaskPath { get; init; } = "";
}

public enum StartupSource
{
    RegistryCurrentUser,
    RegistryLocalMachine,
    /// <summary>
    /// Machine-wide Run key of a 32-bit application on 64-bit Windows
    /// (<c>SOFTWARE\Wow6432Node\...\CurrentVersion\Run</c>) — approved-state in
    /// <c>StartupApproved\Run32</c>, NOT <c>StartupApproved\Run</c>.
    /// <para>A distinct source rather than a flag on <see cref="RegistryLocalMachine"/> because the
    /// enable/disable state lives in a different key: writing the disable blob to
    /// <c>StartupApproved\Run</c> for one of these items would put it where Windows never looks, so the
    /// item would still run at boot while the UI reported "Disabled" — the same failure the Common
    /// startup folder had before <see cref="CommonStartupFolder"/> was split out.</para>
    /// </summary>
    RegistryLocalMachine32,
    /// <summary>Per-user shell Startup folder (%AppData%\...\Startup) — approved-state in HKCU.</summary>
    StartupFolder,
    /// <summary>All-users (Common) shell Startup folder (%ProgramData%\...\Startup) — approved-state in HKLM.</summary>
    CommonStartupFolder,
    TaskScheduler,
    /// <summary>
    /// The policy Run key in either hive
    /// (<c>SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run</c>) — <b>no</b> approved-state
    /// anywhere, so it can be shown but not toggled here.
    /// <para>Task Manager's Startup tab does not list this key at all, which is why bundleware favours it:
    /// the user disables everything visible, reboots, and the program still starts. Showing it is the
    /// point; pretending it can be switched off from here would be the defect.</para>
    /// <para>A distinct source for the same reason as <see cref="RegistryLocalMachine32"/> and
    /// <see cref="CommonStartupFolder"/>: Windows never consults <c>StartupApproved</c> for a policy key,
    /// so a disable blob written there would land where nothing reads it and the item would keep running
    /// while the UI reported "Disabled". <c>SetEnabledAsync</c> therefore refuses this source outright and
    /// says why, the same way it already refuses RunOnce.</para>
    /// </summary>
    PolicyRun
}
