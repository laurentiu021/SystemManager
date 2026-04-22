// SysManager · NavItem
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Windows.Controls;

namespace SysManager.ViewModels;

/// <summary>
/// A single entry in the left nav. View is materialised lazily on first
/// access — keeps unit tests STA-free and makes the VM cheap to construct.
/// </summary>
public sealed class NavItem
{
    private UserControl? _view;

    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Glyph { get; init; }
    public required object Content { get; init; }
    public required Type ViewType { get; init; }

    public UserControl View
    {
        get
        {
            if (_view != null) return _view;
            _view = (UserControl)Activator.CreateInstance(ViewType)!;
            _view.DataContext = Content;
            return _view;
        }
    }
}
