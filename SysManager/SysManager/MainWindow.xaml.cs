// SysManager — MainWindow shell
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SysManager.ViewModels;

namespace SysManager;

public partial class MainWindow : Window
{
    private const int WM_NCACTIVATE = 0x0086;

    public MainWindow()
    {
        InitializeComponent();
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
            source.AddHook(WndProc);
    }

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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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
}
