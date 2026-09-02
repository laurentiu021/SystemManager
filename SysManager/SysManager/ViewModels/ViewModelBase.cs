// SysManager · ViewModelBase
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace SysManager.ViewModels;

public abstract partial class ViewModelBase : ObservableObject, IDisposable
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _progress; // 0-100
    [ObservableProperty] private bool _isProgressIndeterminate;

    /// <summary>
    /// True from the moment <see cref="Dispose()"/> is entered, before any derived override runs.
    /// <para>An async command that resumes after a tab closes must not touch state a
    /// <c>Dispose(bool)</c> override has already released — the recurring failure here was a
    /// <see cref="SemaphoreSlim"/> disposed while an in-flight command still held it. Derived classes
    /// check this before awaiting and again after every await.</para>
    /// <para>Set in the public <see cref="Dispose()"/> rather than in <c>Dispose(bool)</c> on purpose:
    /// overrides call <c>base.Dispose(disposing)</c> <em>last</em>, so a flag set there would still be
    /// false while the override was releasing everything — exactly the window that needs guarding.</para>
    /// </summary>
    protected bool IsDisposed { get; private set; }

    /// <summary>
    /// The command Escape should run on this tab, or <c>null</c> when there is nothing to stop.
    /// </summary>
    /// <remarks>
    /// One property rather than a "can cancel" flag beside a command, because the two must not be
    /// separable. The app answers "is something running?" five different ways — <c>IsBusy</c> on twelve
    /// tabs, and <c>IsShredding</c>, <c>IsScanning</c>, <c>IsHttpTesting</c> and <c>IsOoklaTesting</c> on
    /// the rest — so a shell that tested <c>IsBusy</c> would silently skip four of them. Returning the
    /// command only while busy puts the flag and the command in one expression, in the view model that
    /// owns both.
    /// <para>Defaults to null, which is the correct default rather than a gap: a tab with nothing to
    /// cancel must not swallow the key. Escape then falls through to whatever else would handle it.</para>
    /// </remarks>
    protected internal virtual IRelayCommand? EscapeCancel => null;

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
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
