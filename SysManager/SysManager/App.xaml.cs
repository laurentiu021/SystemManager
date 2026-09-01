// SysManager · App
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SysManager.Models;
using SysManager.Services;

namespace SysManager;

public partial class App : Application
{
    private const string MutexName = "Global\\SysManager_SingleInstance_laurentiu021";
    private const string PipeName = "SysManager_SingleInstance_Pipe_laurentiu021";
    private Mutex? _instanceMutex;

    /// <summary>
    /// True once an exit has been requested programmatically rather than by the user pressing X.
    /// </summary>
    /// <remarks>
    /// Read by <c>MainWindow.OnClosing</c>, which must not ask the close-or-minimise question on this path.
    /// WPF's <see cref="Application.Shutdown()"/> force-closes windows via
    /// <c>Window.InternalClose(shutdown: true, ignoreCancel: true)</c> — that still INVOKES OnClosing and only
    /// ignores the cancel. Without this flag, clicking "Run as administrator" produced a modal asking whether
    /// to keep running in the notification area, and because the single-instance mutex is released in
    /// <c>OnExit</c> — which cannot run while OnClosing waits on a modal — the elevated instance's
    /// <see cref="TryWaitForMutexHandover"/> timed out and the relaunch silently did nothing.
    /// <para>volatile because it is written on the UI thread and read during a shutdown that WPF may drive
    /// from a different one.</para>
    /// </remarks>
    internal static volatile bool ExitRequested;

    /// <summary>
    /// The single way the app exits itself. Records the intent, then shuts down.
    /// </summary>
    /// <remarks>
    /// Every programmatic exit goes through here — the admin relaunches in the view models and the tray icon's
    /// Exit command — so that pressing X remains the only route that can reach the close-or-minimise prompt. A
    /// fitness function asserts no view model calls <c>Application.Current?.Shutdown()</c> directly, because
    /// this was already true of 38 sites and nothing prevented a 39th.
    /// </remarks>
    internal static void RequestShutdown()
    {
        ExitRequested = true;
        Current?.Shutdown();
    }
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

    // Console attach for headless CLI mode: a WinExe has no console of its own, so
    // attach to the parent (the cmd/PowerShell that launched it) to write output there.
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int dwProcessId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    private const int AttachParentProcess = -1;
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

        // Headless CLI mode: when launched with a recognized CLI verb, run it and exit
        // without a window, mutex, or DI graph. Runs before the single-instance guard so
        // a script can run `SysManager.exe --cleanup` while the GUI is already open.
        if (CliRunner.IsCliInvocation(e.Args))
        {
            RunCliAndExit(e.Args);
            return;
        }

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

        // Wire the crash handlers FIRST — before the DI build, tray, and resource-history
        // start below — so a throw anywhere in the remaining startup is logged rather than
        // surfacing as a bare Windows Error Reporting crash with no diagnostic trail.
        DispatcherUnhandledException += OnUi;
        AppDomain.CurrentDomain.UnhandledException += OnDomain;
        TaskScheduler.UnobservedTaskException += OnTask;

        // ── Build DI container ─────────────────────────────────────────
        var serviceCollection = new ServiceCollection();
        serviceCollection.ConfigureServices();
        Services = serviceCollection.BuildServiceProvider();

        // Don't shutdown when main window is hidden (tray mode)
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Initialize tray icon service from DI
        _trayService = Services.GetRequiredService<TrayIconService>();

        // Start the always-on resource history sampler so usage/temperature trends
        // accrue for the whole session, including while minimized to the tray. The
        // service is disposed with the DI container on exit (stops the loop).
        Services.GetRequiredService<ResourceHistoryService>().Start();

        base.OnStartup(e);

        ThemeService.Instance.Initialize();

        // Start listening for activation requests from subsequent instances.
        // Fire-and-forget is intentional — the listener loop runs for the app
        // lifetime and is cancelled via _pipeCts on OnExit.
        _ = StartPipeListenerAsync();
    }

    /// <summary>
    /// Runs the headless CLI and exits with the command's exit code. Attaches to the parent
    /// console so output appears in the launching shell; output is written there, not to a
    /// window. No DI container or window is created. <c>--silent</c> suppresses stdout for
    /// successful commands (the exit code still conveys the result to scripts).
    /// </summary>
    private void RunCliAndExit(string[] args)
    {
        var request = CliRunner.Parse(args);
        bool attached = AttachConsole(AttachParentProcess);
        // Default to the error code (1), not the usage code (2): ExecuteAsync sets the
        // real code for every known command, so the only way we keep this default is an
        // unexpected throw — which is a runtime error, not a usage mistake.
        int exitCode = CliResult.Error;
        try
        {
            var result = new CliRunner().ExecuteAsync(request).GetAwaiter().GetResult();
            exitCode = result.ExitCode;
            bool suppress = request.Silent && result.ExitCode == CliResult.Ok;
            if (attached && !suppress && !string.IsNullOrEmpty(result.Output))
                Console.Out.WriteLine(result.Output);
        }
        catch (Exception ex)
        {
            // A known command never throws (CliRunner reports failures in the result),
            // so reaching here means an unexpected fault. Report it as an error (exit 1)
            // and, if a JSON payload was requested, keep the output machine-readable.
            LogService.Logger?.Error(ex, "CLI command threw unexpectedly");
            if (attached)
                Console.Error.WriteLine(request.Json
                    ? $"{{\"error\":\"{ex.Message.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"}}"
                    : $"Error: {ex.Message}");
        }
        finally
        {
            if (attached) FreeConsole();
            Shutdown(exitCode);
            Environment.Exit(exitCode);
        }
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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Restrict the single-instance pipe to the current user only. The
                // connection carries no payload (it is a pure "activate the window"
                // signal), but an explicit DACL stops any other local account from
                // poking the server, rather than relying on the default ACL.
                await using var server = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0,
                    CreateCurrentUserPipeSecurity());
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
            catch (OperationCanceledException) { break; }   // shutdown
            catch (IOException) { break; }                  // pipe broken during shutdown
            // A transient per-iteration fault (e.g. the OS refuses a pipe instance, or an
            // ObjectDisposedException on a torn-down handle) must NOT permanently kill
            // single-instance activation for the rest of the session — the old single-try
            // wrapping the whole loop did exactly that. Log it and keep listening.
            catch (Exception ex)
            {
                LogService.Logger?.Warning(ex, "Single-instance pipe listener iteration failed; continuing to listen");
            }
        }
    }

    /// <summary>
    /// Builds a <see cref="PipeSecurity"/> that grants only the current user the right
    /// to read/write/connect to the single-instance pipe, so no other local account can
    /// interact with it. Returns null if the current identity can't be resolved, in
    /// which case the caller falls back to the default ACL.
    /// </summary>
    private static System.IO.Pipes.PipeSecurity CreateCurrentUserPipeSecurity()
    {
        var security = new System.IO.Pipes.PipeSecurity();
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var user = identity.User;
        if (user is not null)
        {
            security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                user,
                System.IO.Pipes.PipeAccessRights.ReadWrite,
                System.Security.AccessControl.AccessControlType.Allow));
        }
        return security;
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
            MessageBox.Show(BuildCrashMessage(e.Exception), "SysManager error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _errorDialogActive, 0);
        }
    }

    /// <summary>
    /// Composes the text shown in the last-resort crash dialog: what happened, that the app keeps
    /// running, and WHERE the technical details were written.
    /// <para>The dialog used to show <c>Exception.Message</c> alone, which for the commonest fault
    /// reads "Object reference not set to an instance of an object." — a sentence that tells the user
    /// nothing they can act on and does not mention that a log exists at all. The app knew exactly
    /// where the evidence went and did not say, so a report arrived as "it just closed" with nothing
    /// attached.</para>
    /// <para>Deliberately built from string literals, the exception text, and one static property.
    /// It must not touch DI, the theme, or any service: the unhandled exception may have come from
    /// any of them, so anything else risks faulting inside the crash handler itself. Pure and
    /// internal so it can be unit-tested without raising a real dispatcher exception.</para>
    /// </summary>
    internal static string BuildCrashMessage(Exception? ex)
    {
        // Some frameworks surface a wrapper whose own message is generic; the inner one is what
        // actually describes the fault, so prefer it when present.
        var detail = ex?.InnerException?.Message is { Length: > 0 } inner
            ? inner
            : ex?.Message;
        if (string.IsNullOrWhiteSpace(detail))
            detail = "An unexpected error occurred.";

        return $"""
            Something went wrong, but SysManager is still running.

            {detail}

            Technical details were saved to:
            {LogService.LogDir}

            If this keeps happening, that folder is what to attach when reporting it.
            """;
    }

    private static void OnDomain(object s, UnhandledExceptionEventArgs e)
    {
        LogService.Logger?.Error(e.ExceptionObject as Exception, "Domain exception");

        // A domain-level unhandled exception kills the process with no UI at all, so nothing told
        // the user (or the next launch) that the previous session ended abnormally. Leave a marker so
        // the next start can surface it — this is the only chance to record a fault that took the
        // whole process down. Never written from OnUi, which handles the exception and keeps running.
        WriteCrashMarker(e.ExceptionObject as Exception);
    }

    /// <summary>
    /// Records that this process died from an unhandled exception, as JSON in
    /// <c>%LocalAppData%\SysManager\last-crash.json</c>.
    /// <para>Runs inside a dying process, so it is wrapped tightly and must never throw: an exception
    /// escaping here would replace a logged crash with a silent one. Best-effort by design — if the
    /// write fails, the crash is still in the log.</para>
    /// </summary>
    private static void WriteCrashMarker(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "last-crash.json"), BuildCrashMarker(ex, DateTimeOffset.UtcNow));
        }
        // Deliberately broad: this runs while the process is tearing down, where the usual specific
        // set (IO/Unauthorized) is not exhaustive — a security or serialization fault here must not
        // turn a recorded crash into a silent exit. The crash itself is already in the log above.
        catch (Exception writeFailure)
        {
            LogService.Logger?.Debug("Could not write the crash marker: {Error}", writeFailure.Message);
        }
    }

    /// <summary>
    /// Serializes the crash marker. Pure and internal so the shape is unit-testable without killing a
    /// process; the timestamp is injected rather than read from the clock for the same reason.
    /// <para>Goes through <see cref="CrashMarkerService.Serialize"/> and the shared
    /// <see cref="CrashMarker"/> record rather than an anonymous object, so the writer here and the
    /// reader on next launch cannot drift apart into a file that parses to nothing.</para>
    /// </summary>
    internal static string BuildCrashMarker(Exception? ex, DateTimeOffset whenUtc) =>
        CrashMarkerService.Serialize(new CrashMarker(
            whenUtc,
            // The same source the About tab and the updater use, so a marker can be matched against a
            // release. Reading it is a static assembly-name lookup — no service involved.
            UpdateService.CurrentVersion.ToString(3),
            ex?.GetType().FullName ?? "(unknown)",
            // The message only — never the stack trace or any path. The full detail is in the log,
            // which is scrubbed of the user name on the way to disk; this file exists to answer
            // "did the last run crash?", not to duplicate the log.
            ex?.Message ?? ""));

    private static void OnTask(object? s, UnobservedTaskExceptionEventArgs e)
    {
        LogService.Logger?.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}
