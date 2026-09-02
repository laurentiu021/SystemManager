// SysManager · MainWindow
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

    private readonly ClosePreferenceService _closePreference = new();
    // Per-session: the "it's still running" hint is useful the first time the window
    // disappears, noise on every subsequent close within the same session.
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();

        // Ensure ViewModel disposal even if OnClosed is not called (e.g. app shutdown)
        if (Application.Current != null)
            Application.Current.Exit += OnApplicationExit;

        ToastService.Instance.ToastRequested += OnToastRequested;
        ToastService.Instance.DismissRequested += OnToastDismiss;

        // Closing to the tray calls Hide(), which does NOT deselect the open tab — so its poll loop
        // kept sampling once a second for as long as the PC stayed on. IsVisibleChanged is the right
        // hook rather than OnClosing: it also fires on Show(), and the tray's "Volume mixer" item
        // shows the window BEFORE it navigates, so the flag must already be true by then or that tab
        // would open paused.
        IsVisibleChanged += (_, e) =>
        {
            if (DataContext is MainWindowViewModel vm && e.NewValue is bool visible)
                // Same combined condition as OnStateChanged: a window that is shown but still
                // minimized is not on screen either, so one hook must never contradict the other.
                vm.IsWindowVisible = visible && WindowState != WindowState.Minimized;
        };
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

    private void DismissToast_Click(object sender, RoutedEventArgs e)
    {
        ToastService.Instance.Dismiss();
    }

    private void OnApplicationExit(object sender, ExitEventArgs e)
    {
        (DataContext as MainWindowViewModel)?.Dispose();
    }

    /// <summary>
    /// Pauses the open tab's poll loop while the window is minimized, and resumes it on restore.
    /// <para>Separate from the <c>IsVisibleChanged</c> hook in the constructor because WPF keeps
    /// <see cref="UIElement.IsVisible"/> true for a minimized window — so minimizing to the taskbar
    /// alone would not have triggered it.</para>
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (DataContext is MainWindowViewModel vm)
            vm.IsWindowVisible = IsVisible && WindowState != WindowState.Minimized;
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
            ApplyTitleBarTheme(source.Handle, ThemeService.Instance.CurrentTheme.IsDark);
            // Re-apply on every switch: the OS title bar is not a WPF brush, so nothing else
            // repaints it. Unsubscribed in OnClosed.
            ThemeService.Instance.ThemeChanged += OnThemeChangedForTitleBar;
        }

        // Initialize tray icon after window handle is available. Pass a navigation callback so the
        // tray's "Volume mixer" shortcut can jump to that tab — the View layer legitimately knows
        // the shell view-model, keeping the tray service itself free of a ViewModels dependency.
        if (Application.Current is App app && app.TrayService != null)
            app.TrayService.Initialize(this, navId =>
            {
                if (DataContext is ViewModels.MainWindowViewModel vm) vm.NavigateTo(navId);
            });
    }

    private void OnThemeChangedForTitleBar()
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
            ApplyTitleBarTheme(source.Handle, ThemeService.Instance.CurrentTheme.IsDark);
    }

    /// <summary>
    /// Tells the OS whether to draw the title bar dark or light.
    /// <para>This used to pass a hardcoded 1 and run once at startup, so the title bar stayed dark
    /// for the life of the process — on any of the six light presets the app was a near-white window
    /// wearing a black title bar, the one piece of chrome the user could not theme. It is also the
    /// only surface left pinned to dark after every brush, chart paint and control template was
    /// migrated to invert per preset.</para>
    /// <para>The return value is intentionally discarded: the attribute is unsupported before
    /// Windows 10 1809, and failing to tint a title bar must never take the window down.</para>
    /// </summary>
    private static void ApplyTitleBarTheme(IntPtr hwnd, bool dark)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        int value = dark ? 1 : 0;   // 0 is the documented default (light)
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

    /// <summary>Activate the flat Dashboard navigation button.</summary>
    private void SingleGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is NavItem item
            && DataContext is MainWindowViewModel vm)
            vm.SelectedNav = item;
    }

    /// <summary>Activate a tab inside an expanded navigation group.</summary>
    private void NavChild_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is NavItem item
            && DataContext is MainWindowViewModel vm)
            vm.SelectedNav = item;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing used to hide to the tray unconditionally, because MinimizeToTray defaults
        // to true and was never surfaced anywhere. Pressing X therefore looked like it had
        // closed the app while it kept running, with no notice and no way to change it —
        // the worst outcome for a non-technical user, who then wonders why it is still there.
        //
        // Ask once, remember the answer, and honour it silently afterwards. The tray icon's
        // own Exit command still quits directly, since that intent is already unambiguous.

        // A programmatic exit has already decided. Application.Shutdown() force-closes windows with
        // ignoreCancel: true, which still calls this override — so without this early return, clicking
        // "Run as administrator" (or the tray's Exit) asked the user whether to keep running in the
        // notification area, and the modal held the single-instance mutex past the incoming elevated
        // instance's handover wait. Pressing X is now the only caller that can reach the prompt.
        if (App.ExitRequested)
        {
            base.OnClosing(e);
            return;
        }

        if (Application.Current is not App app || app.TrayService is null)
        {
            base.OnClosing(e);
            return;
        }

        var behavior = _closePreference.Load();
        CloseChoice? answer = null;
        if (behavior == CloseBehavior.Ask)
        {
            answer = DialogService.Instance.AskCloseOrMinimize(
                "SysManager can keep running in the notification area (the icons next to the "
                + "clock) so it continues watching your system, or it can close completely.\n\n"
                + "Yes — keep it running in the notification area\n"
                + "No — close SysManager\n"
                + "Cancel — go back\n\n"
                + "This choice is remembered. You can right-click the notification-area icon "
                + "to reopen the window or exit at any time.",
                "Close SysManager?");

            // Cancel saves nothing — the user has not chosen a behaviour, so they must be asked again.
            // Expressed as a nullable rather than an early return so the save rule sits next to the
            // resolve rule and both are unit-testable.
            if (CloseDecision.PreferenceToSave(answer.Value) is { } chosen)
                _closePreference.Save(chosen);
        }

        switch (CloseDecision.Resolve(behavior, answer))
        {
            case CloseAction.KeepOpen:
                e.Cancel = true;
                return;

            case CloseAction.HideToTray:
                e.Cancel = true;
                TrayIconService.HideWindow(this);
                // Say where the window went the first time it happens. Without this the window
                // simply vanishes, which reads as a crash rather than as running in the tray.
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    ToastService.Instance.Show(
                        "SysManager is still running",
                        "Find it next to the clock. Right-click the icon to reopen or exit.");
                }
                return;

            default:
                // Closing the window is NOT enough to end the process: App sets
                // ShutdownMode.OnExplicitShutdown so SysManager can live in the notification area,
                // which means WPF never exits on its own when the last window closes. Every other exit
                // path calls Shutdown (the tray's Exit item, every RelaunchAsAdmin handler) — this one
                // fell through to base.OnClosing and left the process running with no window AND no
                // tray icon, because the icon is disposed in App.OnExit, which nothing had triggered.
                // The single-instance mutex stayed held too, so the next launch handed itself over to
                // an invisible instance and quit; and because the answer above is REMEMBERED, that
                // repeated on every launch until the user found it in Task Manager.
                App.RequestShutdown();
                base.OnClosing(e);
                return;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // ThemeService is a singleton that outlives the window, so a surviving subscription would
        // keep this instance alive and fire against a dead handle on the next theme switch.
        ThemeService.Instance.ThemeChanged -= OnThemeChangedForTitleBar;
        (DataContext as MainWindowViewModel)?.Dispose();
        base.OnClosed(e);
    }

    private void ThemeBtn_Click(object sender, MouseButtonEventArgs e) => ToggleThemePopup();

    // Enter/Space activate the theme chip for keyboard users. The chip is a Border, so it gets
    // neither for free — and a Button would only have given it Space, since ButtonBase treats Enter
    // as a click only where KeyboardNavigation.AcceptsReturn is set.
    private void ThemeBtn_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            ToggleThemePopup();
            e.Handled = true;
        }
    }

    private void ToggleThemePopup()
    {
        if (ThemePopupHost.Child is null)
        {
            var popup = new Views.ThemePopup();
            // Escape is handled on the CHILD, not on the Popup. With AllowsTransparency the popup lives
            // in its own PresentationSource, so key events route through the child's tree; a handler on
            // the Popup element itself never sees them.
            popup.PreviewKeyDown += ThemePopup_PreviewKeyDown;
            ThemePopupHost.Child = popup;
        }

        ThemePopupHost.IsOpen = !ThemePopupHost.IsOpen;
    }

    // Focus is moved on Opened rather than straight after setting IsOpen: at that point the child has
    // been realised, so there is something focusable to move to.
    private void ThemePopupHost_Opened(object sender, EventArgs e) =>
        ThemePopupHost.Child?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

    // Covers both exits — Escape below and the outside click StaysOpen="False" already handles. Without
    // it, focus is left on a panel that no longer exists and the next Tab restarts from the top of the
    // window. Selecting a preset does NOT close the popup, so this does not fight trying several themes.
    private void ThemePopupHost_Closed(object sender, EventArgs e) => ThemeBtn.Focus();

    /// <summary>
    /// Escape stops whatever the open tab is doing.
    /// </summary>
    /// <remarks>
    /// Bubbling <c>KeyDown</c>, deliberately not <c>PreviewKeyDown</c>. A ComboBox closing its dropdown
    /// and a TextBox reverting an edit both consume Escape and both come first; this only ever sees the
    /// key nothing else wanted. The theme popup is likewise unaffected — it handles Escape on its own
    /// child and marks it handled.
    /// <para>Nothing happens unless the tab returns a command, which its view model does only while it
    /// has something running. Cancelling is always the safe direction — every cancel path here is a
    /// CancellationToken the services already honour — so unlike a destructive action this is a
    /// legitimate thing for a bare keypress to do.</para>
    /// <para><c>IsContentCreated</c> is checked so this cannot construct a view model as a side effect of
    /// a keypress. In practice the selected tab is always materialised; relying on that rather than
    /// asserting it is how a lazy graph gets built by accident.</para>
    /// </remarks>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Escape) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.SelectedNav is not { IsContentCreated: true, Content: ViewModelBase active }) return;
        if (active.EscapeCancel is not { } cancel) return;

        cancel.Execute(null);
        e.Handled = true;
    }

    private void ThemePopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Escape) return;

        ThemePopupHost.IsOpen = false;
        e.Handled = true;
    }
}
