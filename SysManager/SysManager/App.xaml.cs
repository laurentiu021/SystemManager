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
        // Register OEM/ANSI code pages (437, 852, etc.) required by system
        // tools like chkdsk.exe, sfc.exe, and DISM.exe on .NET 8+.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
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

        // Start listening for activation requests from subsequent instances
        StartPipeListener();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        _trayService?.Dispose();
        (Services as IDisposable)?.Dispose();
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
    /// activates the main window when one connects.
    /// </summary>
    private async void StartPipeListener()
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

        // Prevent cascading dialogs: if one is already showing, swallow silently.
        if (System.Threading.Interlocked.CompareExchange(ref _errorDialogActive, 1, 0) != 0)
            return;

        try
        {
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
