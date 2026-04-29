// SysManager — Windows system monitoring toolkit
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT
// Author : laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using SysManager.Services;

namespace SysManager;

public partial class App : Application
{
    private const string MutexName = "Global\\SysManager_SingleInstance_laurentiu021";
    private static Mutex? _instanceMutex;

    // Guard against cascading error dialogs — show at most one at a time.
    private static int _errorDialogActive;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        LogService.Init();
        DispatcherUnhandledException += OnUi;
        AppDomain.CurrentDomain.UnhandledException += OnDomain;
        TaskScheduler.UnobservedTaskException += OnTask;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogService.Shutdown();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void ActivateExistingInstance()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var proc in Process.GetProcessesByName(current.ProcessName))
        {
            try
            {
                if (proc.Id != current.Id && proc.MainWindowHandle != IntPtr.Zero)
                {
                    if (IsIconic(proc.MainWindowHandle))
                        ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(proc.MainWindowHandle);
                    break;
                }
            }
            finally { proc.Dispose(); }
        }
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
