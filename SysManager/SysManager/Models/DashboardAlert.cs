// SysManager · DashboardAlert — represents a system alert on the Dashboard
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

public enum AlertSeverity { Green, Yellow, Red }
public enum AlertLoadingState { Loading, Complete }

public sealed partial class DashboardAlert : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private AlertSeverity _severity = AlertSeverity.Green;
    [ObservableProperty] private AlertLoadingState _state = AlertLoadingState.Loading;
    [ObservableProperty] private string _eta = "";
    [ObservableProperty] private bool _showEta;

    /// <summary>
    /// The nav id of the tab that can act on this finding, or empty when there is nothing to open.
    /// </summary>
    /// <remarks>
    /// The Dashboard is the landing page and the only triage surface, and its alerts used to be inert
    /// strings — two of them literally read "check System Health" without being able to take anyone there
    /// (#1496). Telling someone their disk is failing and leaving them to find the right tab among 58
    /// behind 11 collapsed groups creates the anxiety without the path.
    /// <para>An id rather than a command: a model carrying an <c>ICommand</c> would put the shell inside
    /// the model, while a string keeps the model a description of the finding. The view model owns the
    /// navigation, and <c>ArchitectureTests.EveryNavIdWrittenInTheApp_ResolvesToARealTab</c> pins that
    /// these ids resolve.</para>
    /// <para>That an id RESOLVES is not the same as it being the RIGHT one. This property was assigned
    /// twice in a row for the Event Log alert, with two different real tabs, and the resolve guard was
    /// satisfied by both (#2359) — so the button opened a page that did not list the events it counted.
    /// <c>NoPropertyIsAssignedTwiceInARow</c> covers that half.</para>
    /// </remarks>
    [ObservableProperty] private string _navTargetId = "";

    /// <summary>True when this alert has somewhere to send the user, so the view can hide the button.</summary>
    /// <remarks>
    /// The notification below is load-bearing: the target is assigned AFTER the alert is created, on the
    /// dispatcher once its scan finishes, so without it the button's Visibility binding would never
    /// re-evaluate and every finding would render a hidden link.
    /// </remarks>
    public bool CanNavigate => !string.IsNullOrEmpty(NavTargetId);

    // No NavFilter here. One was added speculatively — "an alert might want to pre-filter its
    // destination" — and EveryModelProperty_IsEitherWrittenOrShown rejected it: no alert scan sets one,
    // so it could only ever have carried an empty string to nobody. Boot Analyzer's rows do pass a
    // filter, through their own model, where it is actually used.
    partial void OnNavTargetIdChanged(string value) => OnPropertyChanged(nameof(CanNavigate));
}
