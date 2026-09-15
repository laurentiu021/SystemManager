// SysManager · INavigationService — the one way a tab asks the shell to open another tab
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Serilog;

namespace SysManager.Services;

/// <summary>
/// Lets a view model send the user to another tab, so a tab that diagnoses something can offer the
/// tab that fixes it.
/// </summary>
/// <remarks>
/// Before this, the one view model that did it reached through
/// <c>Application.Current.MainWindow.DataContext</c> and cast it to the shell — a service locator that
/// cannot be tested and only works while a window happens to be up. Copying that to a second and third
/// caller is what #1504 warned against, so the seam came first: the callers depend on this interface and
/// a test substitutes it.
/// <para>Deliberately just an id and an optional filter. A richer contract — "navigate and select row
/// X", "navigate and start a scan" — would make every destination's internals part of this interface.
/// The filter is the exception because it is the difference between arriving at a list of 200 services
/// and arriving at the one service the user was told about, and every destination that can honour it
/// already has the property.</para>
/// </remarks>
public interface INavigationService
{
    /// <summary>
    /// Opens the tab with this nav id. Unknown ids are ignored rather than throwing: a stale id is a
    /// dead link, which is a defect to fix, not a reason to crash the app under the user's click.
    /// </summary>
    /// <param name="navId">The nav id, e.g. <c>nav-system-health</c>.</param>
    /// <param name="filter">
    /// Optional text to pre-fill the destination's search box, applied only when the destination
    /// implements <see cref="IFilterable"/>. Ignored otherwise, so a caller may always pass it.
    /// </param>
    void GoTo(string navId, string? filter = null);
}

/// <summary>
/// A tab whose list can be narrowed by text, so navigation can arrive pre-filtered.
/// </summary>
/// <remarks>
/// Eight view models already carry a <c>FilterText</c>, but only the ones that opt in here can be
/// pre-filtered — the property existing is not consent to have it written from outside.
/// </remarks>
public interface IFilterable
{
    string FilterText { get; set; }
}

/// <summary>
/// The shell, from navigation's point of view. Implemented by <c>MainWindowViewModel</c>.
/// </summary>
public interface INavigationTarget
{
    void NavigateTo(string navId, string? filter = null);
}

/// <summary>
/// Routes navigation requests to the shell, once the shell has said it exists.
/// </summary>
/// <remarks>
/// Late-bound on purpose. The shell builds the tab view models, and those view models take this
/// service — so a constructor dependency on the shell would be a cycle. <see cref="Bind"/> is called
/// by the shell's constructor, which is before any tab can be clicked.
/// <para>An unbound service logs and does nothing rather than throwing. It is unbound only in a test
/// or a designer, where a swallowed navigation is right and an exception would fail unrelated tests.</para>
/// </remarks>
public sealed class NavigationService : INavigationService
{
    private INavigationTarget? _shell;

    /// <summary>Called once by the shell when it is constructed.</summary>
    public void Bind(INavigationTarget shell) => _shell = shell;

    /// <inheritdoc/>
    public void GoTo(string navId, string? filter = null)
    {
        if (string.IsNullOrWhiteSpace(navId)) return;

        if (_shell is null)
        {
            Log.Debug("Navigation to {NavId} ignored — no shell is bound", navId);
            return;
        }

        _shell.NavigateTo(navId, filter);
    }
}
