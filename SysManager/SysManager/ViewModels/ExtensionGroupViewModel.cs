// SysManager · ExtensionGroupViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using SysManager.Models;

namespace SysManager.ViewModels;

/// <summary>What kind of mark an extension carries next to its name.</summary>
public enum ExtensionFlagKind
{
    /// <summary>Turned off in the browser.</summary>
    Off,

    /// <summary>Another program on the PC added it.</summary>
    AnotherProgram,

    /// <summary>The organisation that manages the PC added it.</summary>
    Organisation,
}

/// <summary>A mark next to an extension's name, such as "Off".</summary>
public sealed record ExtensionFlag(string Text, ExtensionFlagKind Kind)
{
    /// <summary>The mark's words, which is what a screen reader announces for it.</summary>
    public override string ToString() => Text;
}

/// <summary>One extension as its row shows it: every line already worded, and its icon ready to draw.</summary>
public sealed record ExtensionRow(
    string Name,
    string Version,
    string Meta,
    IReadOnlyList<ExtensionFlag> Flags,
    IReadOnlyList<ExtensionPermission> Permissions,
    ImageSource? Icon)
{
    /// <summary>The extension's name and version together, for a screen reader.</summary>
    public string SpokenName => Version.Length > 0 ? $"{Name}, version {Version}" : Name;

    /// <summary>
    /// <see cref="SpokenName"/>: a screen reader announces a list's item by it, and a record's own would read out
    /// every field.
    /// </summary>
    public override string ToString() => SpokenName;
}

/// <summary>
/// One browser profile in Browser Cleaner's Extensions view (#1526): its name and count, its extensions, and
/// "Manage in …", which hands the user to the browser's own extensions page.
/// </summary>
public sealed partial class ExtensionGroupViewModel
{
    private readonly Action<ExtensionProfile> _manage;

    /// <summary>One group for <paramref name="profile"/>, with its rows already worded.</summary>
    /// <param name="manage">Opens the profile's browser on its extensions page; owned by the tab, which reports it.</param>
    public ExtensionGroupViewModel(ExtensionProfile profile, IReadOnlyList<ExtensionRow> rows, Action<ExtensionProfile> manage)
    {
        Profile = profile;
        Rows = rows;
        _manage = manage;
    }

    /// <summary>The profile this group lists.</summary>
    public ExtensionProfile Profile { get; }

    /// <summary>The profile's extensions, in the order the service gave them.</summary>
    public IReadOnlyList<ExtensionRow> Rows { get; }

    /// <summary>The name the tab shows for the profile, e.g. "Microsoft Edge — Profile 1".</summary>
    public string Title => Profile.Browser;

    /// <summary>"5 extensions", or nothing for a profile that could not be read and has none listed.</summary>
    public string CountText => Rows.Count == 0 && Profile.CouldNotRead
        ? ""
        : $"{Rows.Count} extension{(Rows.Count == 1 ? "" : "s")}";

    /// <summary>"Manage in Microsoft Edge".</summary>
    public string ManageText => $"Manage in {Profile.Product}";

    /// <summary>
    /// What to say when the profile's list could not be read, so an empty group is never taken for "no extensions".
    /// Empty when it was read.
    /// </summary>
    public string UnreadableNote => Profile switch
    {
        { BehindALink: true } =>
            $"{Profile.Browser}: its profile is kept in another folder through a link, which SysManager does not follow, so its extensions could not be listed.",
        { CouldNotRead: true } =>
            $"{Profile.Browser}: its extension list could not be read. If {Profile.Product} is open, close it and look again.",
        _ => "",
    };

    /// <summary>The group's title, which is what a screen reader announces for it.</summary>
    public override string ToString() => Title;

    [RelayCommand]
    private void Manage() => _manage(Profile);
}
