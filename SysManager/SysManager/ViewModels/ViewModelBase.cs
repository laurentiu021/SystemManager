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
    /// Completes when the constructor's <see cref="InitializeAsync"/> work has finished
    /// (or immediately if the VM does no async init). Production never awaits this — the
    /// window paints while init runs in the background — but tests can await it to observe
    /// the loaded state deterministically instead of racing the fire-and-forget load.
    /// </summary>
    public Task InitializationComplete { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Safely launches an async task from a constructor or non-async context.
    /// Exceptions are caught and logged instead of becoming unobserved task
    /// exceptions that could crash the application (CQ-M3). The running task is exposed
    /// via <see cref="InitializationComplete"/> for deterministic test observation.
    /// </summary>
    protected void InitializeAsync(Func<Task> asyncAction, [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        InitializationComplete = RunInitAsync(asyncAction, callerName);
    }

    private static async Task RunInitAsync(Func<Task> asyncAction, string callerName)
    {
        try
        {
            await asyncAction().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown — no action needed.
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex, "Invalid operation in async initialization of {Caller}", callerName);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error(ex, "Access denied in async initialization of {Caller}", callerName);
        }
        catch (System.IO.IOException ex)
        {
            Log.Error(ex, "I/O error in async initialization of {Caller}", callerName);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Log.Error(ex, "Network error in async initialization of {Caller}", callerName);
        }
        catch (TimeoutException ex)
        {
            Log.Error(ex, "Timeout in async initialization of {Caller}", callerName);
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
