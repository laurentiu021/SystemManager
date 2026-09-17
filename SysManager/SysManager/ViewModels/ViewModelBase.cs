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
    /// The command F5 should run on this tab, or <c>null</c> when the tab has nothing to re-read.
    /// </summary>
    /// <remarks>
    /// F5 is the most widely known shortcut in Windows and it did nothing anywhere in this app (#1549), on
    /// a tool whose tabs are almost all "go and look again".
    /// <para><b>A property per view model rather than a naming convention the shell matches.</b> The
    /// tabs do not agree on what their refresh is called: measured across the views, 12 distinct spellings
    /// bind to a refresh-shaped button — <c>RefreshCommand</c>, <c>ScanCommand</c>, <c>RescanCommand</c>,
    /// <c>ReloadCommand</c>, <c>LoadHistoryCommand</c>, <c>RefreshDrivesCommand</c>,
    /// <c>RefreshProcessesCommand</c> and more. A shell that matched names would have to guess, and on two
    /// tabs it would have had to pick between two candidates: Deep Cleanup binds both <c>ScanCommand</c>
    /// and <c>ScanLargeFilesCommand</c>, System Health both <c>ScanCommand</c> and
    /// <c>RefreshDrivesCommand</c>. Naming the command here records the decision where it is made, and the
    /// compiler checks it — a renamed command breaks the build instead of silently unbinding the key.</para>
    /// <para><b>Not gated on busy, unlike <see cref="EscapeCancel"/>.</b> Escape must only appear while
    /// there is something to stop, so the flag and the command have to travel together. A refresh is
    /// idempotent and read-only, so the command's own <c>CanExecute</c> is the right gate and the shell
    /// checks it before invoking; a spurious second run costs a re-read, not damage.</para>
    /// <para><b>Read-only commands only.</b> Every tab's F5 target re-reads something — the registry, a
    /// WMI query, a folder, an event log. Nothing that cleans, deletes, applies or kills is reachable from
    /// a bare keypress, and a guard asserts that by requiring the named command to begin with Refresh,
    /// Rescan, Reload, Scan or Load.</para>
    /// </remarks>
    protected internal virtual IRelayCommand? RefreshOnF5 => null;

    /// <summary>
    /// Completes when the constructor's <see cref="InitializeAsync"/> work has finished
    /// (or immediately if the VM does no async init). Production never awaits this — the
    /// window paints while init runs in the background — but tests can await it to observe
    /// the loaded state deterministically instead of racing the fire-and-forget load.
    /// </summary>
    public Task InitializationComplete { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The exception that ended this tab's initialization, or <c>null</c> if init succeeded or was
    /// cancelled during shutdown.
    /// </summary>
    /// <remarks>
    /// The boundary below logs what it catches, and a log line is the only trace a swallowed init fault
    /// leaves. That makes the promise in <see cref="InitializeAsync"/>'s summary unassertable: proving it
    /// through Serilog means assigning the global <c>Log.Logger</c>, which every test in the run shares and
    /// which this suite deliberately never does (see <c>LogServiceLogDirTests</c>). Recording the fault on
    /// the view model makes the same thing observable without touching process-wide state, and states WHICH
    /// exception was caught rather than merely that something was written.
    /// <para>Cancellation is not recorded. A tab closing mid-load is the expected path, not a fault, and
    /// treating it as one would make this property true for almost every tab on shutdown.</para>
    /// </remarks>
    public Exception? InitializationFault { get; private set; }

    /// <summary>
    /// Whether an exception no specific handler in <see cref="InitializeAsync"/> expected is rethrown after
    /// being logged, instead of being swallowed. Defaults to "only under a debugger".
    /// </summary>
    /// <remarks>
    /// #2258 settled on logging at Fatal and rethrowing in a developer build. The condition is
    /// <see cref="System.Diagnostics.Debugger.IsAttached"/> rather than <c>#if DEBUG</c> because every build
    /// this project produces is Release — CI builds Release in all nine of its build steps, and the only
    /// binary anyone runs is the published one — so a <c>#if DEBUG</c> branch would compile into nothing
    /// that ever executes AND could not be covered by a test. A debugger is the accurate spelling of "a
    /// developer is watching", and it leaves both branches compiled and asserted.
    /// <para>Overridable so a test can pin each branch without mutating global state.</para>
    /// </remarks>
    protected internal virtual bool RethrowsUnexpectedInitFaults
        => System.Diagnostics.Debugger.IsAttached;

    /// <summary>
    /// Safely launches an async task from a constructor or non-async context.
    /// Exceptions are caught and logged instead of becoming unobserved task
    /// exceptions that could crash the application (CQ-M3). The running task is exposed
    /// via <see cref="InitializationComplete"/> for deterministic test observation, and whatever ended it
    /// via <see cref="InitializationFault"/>.
    /// </summary>
    protected void InitializeAsync(Func<Task> asyncAction, [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        InitializationComplete = RunInitAsync(asyncAction, callerName);
    }

    private async Task RunInitAsync(Func<Task> asyncAction, string callerName)
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
            InitializationFault = ex;
            Log.Error(ex, "Invalid operation in async initialization of {Caller}", callerName);
        }
        catch (UnauthorizedAccessException ex)
        {
            InitializationFault = ex;
            Log.Error(ex, "Access denied in async initialization of {Caller}", callerName);
        }
        catch (System.IO.IOException ex)
        {
            InitializationFault = ex;
            Log.Error(ex, "I/O error in async initialization of {Caller}", callerName);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            InitializationFault = ex;
            Log.Error(ex, "Network error in async initialization of {Caller}", callerName);
        }
        catch (TimeoutException ex)
        {
            InitializationFault = ex;
            Log.Error(ex, "Timeout in async initialization of {Caller}", callerName);
        }
        // The final net, and the one place in this codebase where catching Exception is the correct
        // answer rather than a tolerated one. The five handlers above name the faults an init is EXPECTED
        // to hit; that list cannot converge, and it did not — it had six entries and missed the first real
        // fault anyone went looking for, a NullReferenceException from DebloaterService.ParsePackages that
        // had been thrown on every launch and reported by nothing (#2258).
        //
        // Without this, the exception is not merely unlogged, it is unobservable. Nothing in production
        // awaits InitializationComplete, so the fault stays on the task; App wires
        // TaskScheduler.UnobservedTaskException, but that fires from the task's FINALIZER, and this task is
        // rooted by InitializationComplete on a view model the nav table caches for the process lifetime.
        // A rooted task is never finalized, so that handler never runs. The tab simply stays half-loaded.
        //
        // Fatal, not Error: the five above describe a specific operation failing, which a tab can be
        // half-useful after. Arriving here means an assumption the code makes about itself was wrong, and
        // whoever reads the log has no other trace that this tab never finished.
        catch (Exception ex)
        {
            InitializationFault = ex;
            Log.Fatal(ex, "Unhandled {Fault} in async initialization of {Caller}; the tab is left "
                        + "half-initialised", ex.GetType().FullName, callerName);
            if (RethrowsUnexpectedInitFaults) throw;
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
