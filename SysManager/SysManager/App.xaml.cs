// SysManager — Windows system monitoring toolkit
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SysManager.Services;

namespace SysManager;

public partial class App : Application
{
    private const string MutexName = "Global\\SysManager_SingleInstance_laurentiu021";
    private const string PipeName = "SysManager_SingleInstance_Pipe_laurentiu021";
    private Mutex? _instanceMutex;
    private TrayIconService? _trayService;
    private CancellationTokenSource? _pipeCts;

    // Guard against cascading error dialogs — show at most one at a time.
    private static int _errorDialogActive;

    /// <summary>The DI service provider for the application.</summary>
    public static IServiceProvider? Services { get; private set; }

    /// <summary>The shared tray icon service instance.</summary>
    public TrayIconService? TrayService => _trayService;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Update applier: when this process was launched by the in-app updater to
        // swap itself over the old executable, do only that and exit — no mutex,
        // no DI, no window. This must run before anything else (and before the
        // single-instance guard, since the old instance may still hold the mutex).
        if (UpdateApplier.TryParseArgs(e.Args, out var targetExe, out var oldPid))
        {
            LogService.Init();
            UpdateApplier.Run(targetExe, oldPid);
            Shutdown();
            return;
        }

        // Register OEM/ANSI code pages (437, 852, etc.) required by system
        // tools like chkdsk.exe, sfc.exe, and DISM.exe on .NET 8+.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // When this instance was started by "Run as administrator", the outgoing
            // non-elevated instance is shutting down but may not have released the mutex
            // yet. Wait briefly for it to hand over instead of treating ourselves as a
            // duplicate — otherwise the elevated copy exits and the user is left on the
            // non-elevated window with the admin banners still showing.
            if (WasRelaunchedElevated(e.Args) && TryWaitForMutexHandover())
            {
                createdNew = true; // acquired after the old instance released it
            }
            else
            {
                ActivateExistingInstance();
                Shutdown();
                return;
            }
        }

        LogService.Init();

        // ── Build DI container ─────────────────────────────────────────
        var serviceCollection = new ServiceCollection();
        serviceCollection.ConfigureServices();
        Services = serviceCollection.BuildServiceProvider();

        // Don't shutdown when main window is hidden (tray mode)
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Initialize tray icon service from DI
        _trayService = Services.GetRequiredService<TrayIconService>();

        DispatcherUnhandledException += OnUi;
        AppDomain.CurrentDomain.UnhandledException += OnDomain;
        TaskScheduler.UnobservedTaskException += OnTask;
        base.OnStartup(e);

        ThemeService.Instance.Initialize();

        // Start listening for activation requests from subsequent instances.
        // Fire-and-forget is intentional — the listener loop runs for the app
        // lifetime and is cancelled via _pipeCts on OnExit.
        _ = StartPipeListenerAsync();
    }

    /// <summary>
    /// True when this instance was started by <see cref="Helpers.AdminHelper.RelaunchAsAdmin"/>
    /// (carries the elevation sentinel argument).
    /// </summary>
    private static bool WasRelaunchedElevated(string[] args)
        => args.Any(a => string.Equals(a, Helpers.AdminHelper.RelaunchedElevatedArg, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Waits up to a few seconds for the outgoing instance to release the single-instance
    /// mutex, then takes ownership. Returns true if the mutex was acquired. The wait covers
    /// the brief window between the old instance calling Shutdown() and its OnExit releasing
    /// the mutex. An <see cref="AbandonedMutexException"/> still means we own it (the previous
    /// owner exited without releasing) — that is success, not failure.
    /// </summary>
    private bool TryWaitForMutexHandover()
    {
        if (_instanceMutex is null) return false;
        try
        {
            return _instanceMutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            // Previous owner exited without releasing — ownership has passed to us.
            return true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        _trayService?.Dispose();
        try { (Services as IDisposable)?.Dispose(); }
        catch (ObjectDisposedException ex) { LogService.Logger?.Debug(ex, "Service provider already disposed at exit"); }
        LogService.Shutdown();
        try { _instanceMutex?.ReleaseMutex(); }
        catch (ApplicationException) { /* mutex not owned by this thread */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void ActivateExistingInstance()
    {
        // Try named pipe first — works even when the window is hidden (tray mode)
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            // Connection alone signals the running instance to activate
        }
        catch (TimeoutException) { /* pipe not available, fall back to window activation */ }
        catch (IOException) { /* pipe not available */ }

        // Fallback: find the window handle (works when window is visible)
        using var current = Process.GetCurrentProcess();
        foreach (var proc in Process.GetProcessesByName(current.ProcessName))
        {
            using (proc)
            {
                if (proc.Id != current.Id && proc.MainWindowHandle != IntPtr.Zero)
                {
                    if (IsIconic(proc.MainWindowHandle))
                        ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(proc.MainWindowHandle);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Listens for named pipe connections from subsequent instances and
    /// activates the main window when one connects. Returns a Task so the
    /// caller can fire-and-forget without using the async-void anti-pattern;
    /// any exception escaping the loop is logged via OnTask (UnobservedTaskException).
    /// </summary>
    private async Task StartPipeListenerAsync()
    {
        _pipeCts = new CancellationTokenSource();
        var ct = _pipeCts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // A second instance connected — activate our window on the UI thread
                _ = Dispatcher.BeginInvoke(() =>
                {
                    var win = MainWindow;
                    if (win != null)
                    {
                        TrayIconService.ShowWindow(win);
                    }
                });
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (IOException) { /* pipe broken during shutdown */ }
    }

    private static void OnUi(object s, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Logger?.Error(e.Exception, "UI thread exception");
        e.Handled = true;

        // Swallow disposed/cancelled exceptions during shutdown — CTS/services
        // being disposed while async operations are still in flight is expected.
        if (e.Exception is ObjectDisposedException)
            return;
        if (e.Exception is InvalidOperationException && e.Exception.Message.Contains("disposed", StringComparison.OrdinalIgnoreCase))
            return;
        if (e.Exception.InnerException is ObjectDisposedException)
            return;
        if (e.Exception is OperationCanceledException)
            return;

        // Prevent cascading dialogs: if one is already showing, swallow silently.
        if (System.Threading.Interlocked.CompareExchange(ref _errorDialogActive, 1, 0) != 0)
            return;

        try
        {
            // MessageBox is the safe last-resort dialog here: the unhandled
            // dispatcher exception may itself originate from DialogService or
            // any of its dependencies, so we cannot rely on the app's own
            // dialog stack at this point. Direct WPF MessageBox always works.
            MessageBox.Show(e.Exception.Message, "SysManager error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _errorDialogActive, 0);
        }
    }

    private static void OnDomain(object s, UnhandledExceptionEventArgs e)
        => LogService.Logger?.Error(e.ExceptionObject as Exception, "Domain exception");

    private static void OnTask(object? s, UnobservedTaskExceptionEventArgs e)
    {
        LogService.Logger?.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}
