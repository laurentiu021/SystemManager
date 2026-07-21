// SysManager · NavItem
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.ViewModels;

/// <summary>
/// A single entry in the left nav. Both the <see cref="View"/> AND the underlying
/// ViewModel (<see cref="Content"/>) are materialised lazily on first access, so the
/// ~55 tab view-models (most of which kick off a background scan / timer in their
/// constructor) are NOT all built at startup — only the tab the user actually opens.
/// Exposes <see cref="IsBusy"/> from the underlying ViewModel so the sidebar can show
/// a progress indicator when the tab is working. Implements <see cref="IDisposable"/>
/// to unsubscribe from ViewModel PropertyChanged events on teardown.
/// </summary>
public sealed partial class NavItem : ObservableObject, IDisposable
{
    private UserControl? _view;
    private object? _content;
    private Func<object>? _contentFactory;

    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Glyph { get; init; }
    public required Type ViewType { get; init; }

    /// <summary>
    /// The tab's ViewModel. Two ways to supply it:
    /// <list type="bullet">
    /// <item>Eager — assign a ready instance (<c>Content = vm</c>). Used by tests and by the
    /// few tabs that must exist at startup (e.g. Dark Mode owns the always-on theme schedule).</item>
    /// <item>Lazy — leave it unset and provide <see cref="ContentFactory"/>; the instance is built
    /// on first access and cached.</item>
    /// </list>
    /// First materialisation (either way, via the getter) wires <see cref="IsBusy"/> forwarding,
    /// so the sidebar spinner works regardless. A tab that is never opened never builds its VM.
    /// </summary>
    public object Content
    {
        get
        {
            if (_content is not null) return _content;

            _content = _contentFactory?.Invoke()
                ?? throw new InvalidOperationException(
                    $"NavItem '{Id}' has neither an eager Content nor a ContentFactory set.");
            WireBusy(_content);
            return _content;
        }
        init
        {
            _content = value;   // eager path — instance provided up front
        }
    }

    /// <summary>
    /// Factory that builds the tab's ViewModel on first <see cref="Content"/> access (lazy path,
    /// e.g. resolve from DI only when the tab is first opened). Ignored if an eager
    /// <see cref="Content"/> instance was assigned.
    /// </summary>
    public Func<object>? ContentFactory
    {
        private get => _contentFactory;
        init => _contentFactory = value;
    }

    /// <summary>
    /// True once <see cref="Content"/> has been materialised. Lets teardown / activation logic
    /// touch the VM without forcing a never-opened tab to build one.
    /// </summary>
    public bool IsContentCreated => _content is not null;

    /// <summary>
    /// True for features that are implemented but not yet QA-verified. The sidebar
    /// shows a small "PREVIEW" pill next to the label, and the view shows a
    /// <see cref="Views.DevelopmentBanner"/> so users know the feature is new.
    /// </summary>
    public bool IsInDevelopment { get; init; }

    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Wire IsBusy forwarding from the underlying ViewModel. Called automatically on first
    /// materialisation of <see cref="Content"/>. For an eagerly-assigned instance that must
    /// forward IsBusy before it is ever displayed, call this after construction.
    /// </summary>
    public NavItem WireBusy()
    {
        if (_content is not null) WireBusy(_content);
        return this;
    }

    private void WireBusy(object content)
    {
        if (content is ViewModelBase vm)
        {
            // Idempotent: -= then += so an eager WireBusy() followed by the getter's wiring
            // (or a double WireBusy) never double-subscribes.
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            IsBusy = vm.IsBusy;
        }
    }

    /// <summary>
    /// Unsubscribe from ViewModel events and dispose the VM — but only if it was ever
    /// materialised. A tab the user never opened built no VM, so there is nothing to release
    /// (and forcing creation here would defeat the whole lazy design).
    /// </summary>
    public void Dispose()
    {
        if (_content is null) return; // never opened → nothing built
        if (_content is ViewModelBase vm)
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        (_content as IDisposable)?.Dispose();
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
            if (_view is not null) return _view;
            _view = (UserControl)Activator.CreateInstance(ViewType)!;
            _view.DataContext = Content; // materialises the VM on first view
            return _view;
        }
    }
}
