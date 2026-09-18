// SysManager · BlockedApp — model for a blocked application
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents an application that has been blocked from executing via IFEO.
/// </summary>
public sealed partial class BlockedApp : ObservableObject, Helpers.ISelectableRow
{
    /// <summary>Executable file name (e.g., "notepad.exe").</summary>
    /// <remarks>
    /// A name, not a path, and deliberately so: an IFEO key is per executable NAME, which is why a block
    /// keeps working wherever the user moves or copies the file. A <c>FullPath</c> property used to sit
    /// beside this one and nothing could ever fill it — the registry does not record where the executable
    /// lived — so it was permanently empty and has been removed rather than left to imply the app knows.
    /// A <c>BlockedAt</c> timestamp was removed for a sharper reason: it was set to <c>DateTime.Now</c>
    /// when the list was READ, so it recorded when you opened the tab, not when you blocked anything.
    /// IFEO stores no creation time, so the honest answer is not to claim one.
    /// </remarks>
    [ObservableProperty] private string _executableName = "";

    /// <summary>Whether this entry is selected in the UI.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// True when lifting this block needs something the block itself took away.
    /// </summary>
    /// <remarks>
    /// Set by the service that reads the list, never by the view, so one rule decides it — see
    /// <c>AppBlockerService.IsUnrecoverableBlock</c>. A build published before #2030 could write an IFEO
    /// block on <c>consent.exe</c>, the UAC consent UI; unblocking writes to HKLM, which needs elevation,
    /// which needs consent.exe. Without this flag such an entry is indistinguishable in the list from a
    /// game launcher somebody chose to block (#2357).
    /// </remarks>
    [ObservableProperty] private bool _isUnrecoverable;
}
