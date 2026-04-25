// SysManager · NavGroup
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.ViewModels;

/// <summary>
/// A collapsible group in the sidebar tree. Contains child <see cref="NavItem"/>
/// entries. Groups that have exactly one child (e.g. Dashboard) are rendered
/// as a single top-level item without expand/collapse chrome.
/// </summary>
public sealed partial class NavGroup : ObservableObject
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Glyph { get; init; }

    [ObservableProperty] private bool _isExpanded = true;

    public ObservableCollection<NavItem> Children { get; init; } = new();

    /// <summary>
    /// True when the group has a single child and should render as a
    /// flat top-level nav item (no expander arrow).
    /// </summary>
    public bool IsSingleItem => Children.Count == 1;
}
