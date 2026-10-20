// SysManager · AppPackage
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

public sealed partial class AppPackage : ObservableObject, Helpers.ISelectableRow
{
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnknownVersion))]
    private string _currentVersion = string.Empty;
    [ObservableProperty] private string _availableVersion = string.Empty;
    [ObservableProperty] private string _source = "winget";
    [ObservableProperty] private string _status = "Pending";

    /// <summary>
    /// What winget prints in the Version column when it cannot read the installed version. It lists such an app only
    /// with <c>--include-unknown</c>, and reinstalling it does not tell winget the version either, so the app comes
    /// back on every scan.
    /// </summary>
    public const string UnknownVersion = "Unknown";

    /// <summary>
    /// True when winget cannot read the installed version, so it cannot tell whether the "update" is newer. Such a row
    /// arrives unticked and is left out of the Dashboard's count.
    /// </summary>
    public bool HasUnknownVersion => string.Equals(CurrentVersion, UnknownVersion, StringComparison.OrdinalIgnoreCase);
}
