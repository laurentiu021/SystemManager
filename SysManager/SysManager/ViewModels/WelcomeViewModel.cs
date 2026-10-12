// SysManager · WelcomeViewModel — the one-time Welcome screen over the window
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// The Welcome screen shown over the window the first time SysManager starts on a PC (#1635).
/// </summary>
/// <remarks>
/// The app used to open straight onto the Dashboard's live sensors, five "Checking…" lines and a sidebar of collapsed
/// groups, and nothing on screen said what it is or that it is safe. This says both, once, and offers two places to
/// start. One screen and no steps: everything on it is a sentence and a way out.
/// <para>Every way out counts as seen: the three buttons, Escape, and closing the window, which the shell reports
/// through <see cref="RememberSeen"/>. Only a launch that ends without any of them, a crash or a killed process,
/// shows it again.</para>
/// <para>Navigation only. Nothing reachable from here cleans, deletes or changes anything: each place it offers is a
/// tab, and that tab asks before it acts, as it always does.</para>
/// </remarks>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly IFirstRunService _firstRun;
    private readonly INavigationService _navigation;

    // Whether this session's record has been written, so the window closing and then a button do not write it twice.
    // A write that failed leaves it false, and the next way out tries again.
    private bool _remembered;

    /// <summary>Whether the Welcome screen is on screen: true only on a PC where it has not been seen.</summary>
    [ObservableProperty] private bool _isVisible;

    public WelcomeViewModel(IFirstRunService firstRun, INavigationService navigation)
    {
        _firstRun = firstRun;
        _navigation = navigation;
        IsVisible = !firstRun.HasSeenWelcome();
    }

    /// <summary>Closes the Welcome screen for good and opens System Health, the safest first step.</summary>
    [RelayCommand]
    private void CheckHealth() => CloseAndOpen("nav-system-health");

    /// <summary>Closes the Welcome screen for good and opens Quick Cleanup.</summary>
    [RelayCommand]
    private void CleanUp() => CloseAndOpen("nav-cleanup");

    /// <summary>Closes the Welcome screen for good and leaves the window on the page it shows, at first the Dashboard.</summary>
    [RelayCommand]
    private void LookAround() => Close();

    /// <summary>
    /// The command a key runs while the Welcome screen is up, or null for a key it ignores.
    /// </summary>
    /// <remarks>
    /// Escape is "Just look around", the one way out that goes nowhere. Pure and <c>internal</c>, the same shape as
    /// the shell's accelerator lookups, so the routing is testable without a window: the shell's <c>KeyDown</c>
    /// handler asks this first and gives no other key to the shell or the tab behind while the screen is up.
    /// </remarks>
    internal IRelayCommand? KeyCommand(Key key) => key is Key.Escape ? LookAroundCommand : null;

    /// <summary>
    /// Records that the Welcome screen has been seen, and leaves it on screen.
    /// </summary>
    /// <remarks>
    /// For the window closing while it is up. The close can still be cancelled at the question that follows, and
    /// then the window should come back exactly as it was, so this does not hide it.
    /// </remarks>
    public void RememberSeen()
    {
        if (!IsVisible || _remembered) return;
        _remembered = _firstRun.MarkWelcomeSeen();
    }

    private void Close()
    {
        RememberSeen();
        IsVisible = false;
    }

    private void CloseAndOpen(string navId)
    {
        Close();
        _navigation.GoTo(navId);
    }
}
