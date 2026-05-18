// SysManager · ViewModelBase
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace SysManager.ViewModels;

public abstract partial class ViewModelBase : ObservableObject, IDisposable
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _progress; // 0-100
    [ObservableProperty] private bool _isProgressIndeterminate;

    private bool _disposed;

    /// <summary>
    /// Safely launches an async task from a constructor or non-async context.
    /// Exceptions are caught and logged instead of becoming unobserved task
    /// exceptions that could crash the application (CQ-M3).
    /// </summary>
    protected static async void InitializeAsync(Func<Task> asyncAction, [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        try
        {
            await asyncAction().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown — no action needed.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled exception in async initialization of {Caller}", callerName);
        }
    }

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
