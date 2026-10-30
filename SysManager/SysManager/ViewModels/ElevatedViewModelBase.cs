// SysManager · ElevatedViewModelBase
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Helpers;

namespace SysManager.ViewModels;

/// <summary>
/// Base for every view model that hosts <c>Views/AdminBanner.xaml</c>: it binds <see cref="IsElevated"/>
/// and <see cref="RelaunchAsAdminCommand"/> ambiently from its host's DataContext, not from properties on
/// the control, so a host that lacked either member rendered a banner stuck in one state or a button that
/// did nothing — with no compiler error and no visible clue (#2860).
/// </summary>
/// <remarks>
/// Before this, 29 view models each declared the identical field and command by hand, one of them (#2859's
/// LogsViewModel) the most recent copy. Three hosts are deliberately NOT here: <c>BootAnalyzerViewModel</c>,
/// <c>StartupViewModel</c> and <c>WindowsUpdateViewModel</c> take elevation as an injected <c>Func&lt;bool&gt;</c>
/// instead of calling <see cref="AdminHelper.IsElevated"/> directly — Startup and Windows Update keep that
/// probe and read it again later, which this base does not support — so migrating them would be a behaviour
/// change, not a deduplication. <c>MainWindowViewModel</c>, <c>UninstallerViewModel</c> and
/// <c>DiskAnalyzerViewModel</c> use <c>IsElevated</c> or <c>RelaunchAsAdmin</c> for something other than
/// hosting this banner and are out of scope for the same reason.
/// </remarks>
public abstract partial class ElevatedViewModelBase : ViewModelBase
{
    [ObservableProperty] private bool _isElevated;

    protected ElevatedViewModelBase() => IsElevated = AdminHelper.IsElevated();

    /// <summary>
    /// In-body elevation gate. Returns false, and says why, when the change cannot run.
    /// </summary>
    /// <remarks>
    /// A command's <c>CanExecute</c> is a UI affordance, not a guard: <c>ExecuteAsync</c> runs the command body
    /// whether or not it agreed, so a change that needs administrator rights checks here, before it asks the user
    /// anything. No relaunch prompt: the tab's AdminBanner already offers one.
    /// </remarks>
    protected bool RequireElevation(string what)
    {
        if (IsElevated) return true;
        StatusMessage = $"{what} needs administrator rights — use \"Run as administrator\" above.";
        return false;
    }

    // Protected, not private: CleanupViewModel and ContextMenuViewModel also call this directly from an
    // inline relaunch prompt elsewhere in their own file, outside the command the banner binds to.
    [RelayCommand]
    protected void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }
}
