// SysManager — Windows system monitoring toolkit
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT
// Author : laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Windows;
using System.Windows.Threading;
using SysManager.Services;

namespace SysManager;

public partial class App : Application
{
    // Guard against cascading error dialogs — show at most one at a time.
    private static int _errorDialogActive;

    protected override void OnStartup(StartupEventArgs e)
    {
        LogService.Init();
        DispatcherUnhandledException += OnUi;
        AppDomain.CurrentDomain.UnhandledException += OnDomain;
        TaskScheduler.UnobservedTaskException += OnTask;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogService.Shutdown();
        base.OnExit(e);
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
