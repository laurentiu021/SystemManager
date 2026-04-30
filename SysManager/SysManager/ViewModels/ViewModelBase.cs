// SysManager · ViewModelBase
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.ViewModels;

public abstract partial class ViewModelBase : ObservableObject, IDisposable
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _progress; // 0-100
    [ObservableProperty] private bool _isProgressIndeterminate;

    private bool _disposed;

    /// <summary>
    /// Override in derived classes to release managed resources
    /// (CancellationTokenSources, event handlers, timers, etc.).
    /// Always call <c>base.Dispose(disposing)</c> at the end.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
