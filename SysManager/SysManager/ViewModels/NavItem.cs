// SysManager · NavItem
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.ComponentModel;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.ViewModels;

/// <summary>
/// A single entry in the left nav. View is materialised lazily on first
/// access — keeps unit tests STA-free and makes the VM cheap to construct.
/// Exposes <see cref="IsBusy"/> from the underlying ViewModel so the sidebar
/// can show a progress indicator when the tab is working.
/// </summary>
public sealed partial class NavItem : ObservableObject
{
    private UserControl? _view;

    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Glyph { get; init; }
    public required object Content { get; init; }
    public required Type ViewType { get; init; }

    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Call after construction to wire up IsBusy forwarding from the ViewModel.
    /// </summary>
    public NavItem WireBusy()
    {
        if (Content is ViewModelBase vm)
            vm.PropertyChanged += OnViewModelPropertyChanged;
        return this;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModelBase.IsBusy) && sender is ViewModelBase vm)
            IsBusy = vm.IsBusy;
    }

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
