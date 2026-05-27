// SysManager — MainWindow shell
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager;

public partial class MainWindow : Window
{
    private const int WM_NCACTIVATE = 0x0086;

    public MainWindow()
    {
        InitializeComponent();

        // Ensure ViewModel disposal even if OnClosed is not called (e.g. app shutdown)
        if (Application.Current != null)
            Application.Current.Exit += OnApplicationExit;

        ToastService.Instance.ToastRequested += OnToastRequested;
        ToastService.Instance.DismissRequested += OnToastDismiss;
    }

    private void OnToastRequested(string title, string detail)
    {
        ToastTitle.Text = title;
        ToastDetail.Text = detail;
        ToastOverlay.Visibility = Visibility.Visible;
        ToastOverlay.Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        ToastOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void OnToastDismiss()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => ToastOverlay.Visibility = Visibility.Collapsed;
        ToastOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void DismissToast_Click(object sender, MouseButtonEventArgs e)
    {
        ToastService.Instance.Dismiss();
    }

    private void OnApplicationExit(object sender, ExitEventArgs e)
    {
        (DataContext as MainWindowViewModel)?.Dispose();
    }

    /// <summary>
    /// Prevents the non-client area (title bar, borders) from visually
    /// dimming when the window loses focus.  This stops ModernWPF's
    /// chrome from graying-out buttons and other controls.
    /// Fixes #252, #251, #248, #245.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
            ApplyDarkTitleBar(source.Handle);
        }

        // Initialize tray icon after window handle is available
        if (Application.Current is App app && app.TrayService != null)
            app.TrayService.Initialize(this);
    }

    private static void ApplyDarkTitleBar(IntPtr hwnd)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        int value = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    [System.Runtime.InteropServices.LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCACTIVATE)
        {
            // Force the non-client area to always render as "active".
            // wParam = 1 means active, 0 means inactive.
            // By always passing TRUE we keep the chrome looking active.
            handled = true;
            return DefWindowProc(hwnd, msg, new IntPtr(1), lParam);
        }
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Click on a single-item group (Dashboard, Network).</summary>
    private void SingleGroup_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is NavItem item
            && DataContext is MainWindowViewModel vm)
            vm.SelectedNav = item;
    }

    /// <summary>Click on a child item inside an expanded group.</summary>
    private void NavChild_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is NavItem item
            && DataContext is MainWindowViewModel vm)
            vm.SelectedNav = item;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Minimize to tray instead of closing (if enabled)
        if (Application.Current is App app && app.TrayService is { MinimizeToTray: true })
        {
            e.Cancel = true;
            TrayIconService.HideWindow(this);
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as MainWindowViewModel)?.Dispose();
        base.OnClosed(e);
    }

    private void ThemeBtn_Click(object sender, MouseButtonEventArgs e)
    {
        if (ThemePopupHost.Child is null)
            ThemePopupHost.Child = new Views.ThemePopup();
        ThemePopupHost.IsOpen = !ThemePopupHost.IsOpen;
    }
}
