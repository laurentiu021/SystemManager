// SysManager · UninstallLeftover — what an uninstall left behind
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>How sure SysManager is that a leftover belonged to the app that was uninstalled.</summary>
public enum LeftoverConfidence
{
    /// <summary>The folder the app's own uninstall entry named as its install location.</summary>
    Certain,

    /// <summary>A folder named exactly after the app, directly under AppData, Local AppData or ProgramData.</summary>
    Probably,

    /// <summary>The app's own registry key, <c>HKCU\Software\&lt;Publisher&gt;\&lt;App&gt;</c>.</summary>
    OwnKey,

    /// <summary>
    /// A folder named after the publisher, directly under AppData or Local AppData, offered only when no other
    /// installed app has that publisher.
    /// </summary>
    Guess,
}

/// <summary>What kind of thing a leftover is.</summary>
public enum LeftoverKind
{
    /// <summary>A folder, sent to the Recycle Bin when removed.</summary>
    Folder,

    /// <summary>A key under <c>HKEY_CURRENT_USER</c>, exported to a <c>.reg</c> file before it is deleted.</summary>
    RegistryKey,
}

/// <summary>
/// What SysManager knew about an app before it was uninstalled, which is all a leftover search can go on.
/// </summary>
/// <remarks>
/// Captured BEFORE the uninstaller starts: some uninstallers delete the app's uninstall entry first, and that entry is
/// where <paramref name="InstallLocation"/> comes from, so a search that read it afterwards would find nothing.
/// </remarks>
/// <param name="Name">The display name the uninstall entry or winget gave.</param>
/// <param name="Publisher">The publisher the uninstall entry named, or empty.</param>
/// <param name="Id">The winget id, or empty for an app found only in the registry.</param>
/// <param name="InstallLocation">The folder the uninstall entry named as its install location, or empty.</param>
/// <param name="UninstallCommand">
/// The command the uninstall entry runs, or empty. For an app still installed, the folder its uninstaller sits in
/// is one more place that app lives, so a leftover search never offers it or a folder around it.
/// </param>
public sealed record UninstallProbe(string Name, string Publisher, string Id, string InstallLocation,
                                    string UninstallCommand = "")
{
    /// <summary>The probe for an app on the list, read from what the scan recorded.</summary>
    public static UninstallProbe Of(InstalledApp app) =>
        new(app.Name ?? "", app.Publisher ?? "", app.Id ?? "", app.InstallLocation ?? "",
            string.IsNullOrWhiteSpace(app.QuietUninstallString) ? app.UninstallString ?? "" : app.QuietUninstallString);
}

/// <summary>One thing an uninstall left behind: a folder or a registry key, with how sure SysManager is of it.</summary>
public sealed partial class LeftoverItem : ObservableObject
{
    /// <summary>Ticked for removal. Only <see cref="LeftoverConfidence.Certain"/> items start ticked.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>The folder's full path, or the key's path under <c>HKEY_CURRENT_USER</c> (for example <c>Software\Pub\App</c>).</summary>
    public required string Location { get; init; }

    public required LeftoverKind Kind { get; init; }

    public required LeftoverConfidence Confidence { get; init; }

    /// <summary>The publisher a <see cref="LeftoverConfidence.Guess"/> was named after, for its reason text.</summary>
    public string Publisher { get; init; } = "";

    /// <summary>The folder's size when it was found. Zero for a registry key.</summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// True when removing it needs administrator rights SysManager does not have right now: a folder under Program
    /// Files or ProgramData, found while SysManager runs without them. Such an item cannot be ticked, and is
    /// remembered so that the Uninstaller offers it again in a session that runs as administrator.
    /// </summary>
    public bool NeedsAdministrator { get; init; }

    /// <summary>Whether the user can tick it now.</summary>
    public bool CanSelect => !NeedsAdministrator;

    /// <summary>The path as shown: <c>HKEY_CURRENT_USER\...</c> for a key, the folder path otherwise.</summary>
    public string DisplayLocation => Kind == LeftoverKind.RegistryKey ? $@"HKEY_CURRENT_USER\{Location}" : Location;

    /// <summary>Why it is on the list, in words.</summary>
    public string Reason => Confidence switch
    {
        LeftoverConfidence.Certain => "The folder it was installed in",
        LeftoverConfidence.Probably => "Named after the app — most likely its settings or cache",
        LeftoverConfidence.OwnKey => "Its settings in the registry — a copy is saved before removal",
        LeftoverConfidence.Guess => $"Named after the publisher, and no other {Publisher} app is installed",
        _ => "",
    };

    /// <summary>The confidence as its pill reads.</summary>
    public string ConfidenceLabel => Confidence switch
    {
        LeftoverConfidence.Certain => "Certain",
        LeftoverConfidence.Probably => "Probably",
        LeftoverConfidence.OwnKey => "Its own key",
        LeftoverConfidence.Guess => "Guess",
        _ => "",
    };

    public string SizeDisplay => Kind == LeftoverKind.RegistryKey ? "—" : FormatHelper.FormatSize(SizeBytes);
}

/// <summary>What one uninstalled app left behind, and what was found but deliberately not offered.</summary>
public sealed class LeftoverGroup
{
    public required string AppName { get; init; }

    public string Publisher { get; init; } = "";

    public ObservableCollection<LeftoverItem> Items { get; init; } = [];

    /// <summary>
    /// One sentence per match that was found and refused, for example a folder in Documents, so the user knows it was
    /// seen and why it is not on the list.
    /// </summary>
    public IReadOnlyList<string> NotOffered { get; init; } = [];

    /// <summary>The line under the app's name.</summary>
    public string Caption => string.IsNullOrWhiteSpace(Publisher) ? "uninstalled" : $"{Publisher} · uninstalled";
}
