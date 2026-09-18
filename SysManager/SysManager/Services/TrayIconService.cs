// SysManager · TrayIconService — system tray icon with background monitoring
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Manages the system tray icon: tooltip updates, context menu, and
/// Windows toast notifications when system health degrades.
/// Runs a background timer (60s) to poll CPU/RAM/disk status.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly SystemInfoService _sysInfo;
    private readonly DispatcherTimer _timer;
    private TaskbarIcon? _trayIcon;
    // The GDI Icon handle we created (via ExtractAssociatedIcon or new Icon(stream)) and
    // must dispose ourselves. Null when we fell back to the shared SystemIcons.Application,
    // which is process-wide and must NOT be disposed.
    private System.Drawing.Icon? _ownedIcon;
    private bool _disposed;
    private int _updating; // PERF-M4: re-entrancy guard for WMI calls

    /// <summary>
    /// The one-line CPU/RAM/uptime readout the context menu shows, refreshed by the same 60-second poll
    /// that writes the tooltip.
    /// </summary>
    /// <remarks>
    /// A field rather than a live-bound item, because the menu is built once and opened many times: the
    /// header's text is set from here in the <c>Opened</c> handler, so it is current when the menu appears
    /// without anything being rebuilt on every tick.
    /// <para>No synchronisation. The timer is a <see cref="DispatcherTimer"/>, so the write happens on the UI
    /// thread, and the menu opening reads it on that same thread.</para>
    /// <para><c>internal</c> rather than private so a test can stand in for a completed poll. The real writer
    /// is <see cref="UpdateTooltipAsync"/>, which returns early when there is no tray icon — so without a
    /// shell-registered icon the value can never change, and the refresh-on-open behaviour would be
    /// untestable without either creating one or exposing this.</para>
    /// </remarks>
    internal string MenuStatusLine { get; set; } = "Reading system status…";

    /// <summary>The menu item that shows <see cref="MenuStatusLine"/>, or null before the menu is built.</summary>
    private System.Windows.Controls.MenuItem? _statusItem;

    // Notification cooldowns — don't spam the user
    private DateTime _lastRamNotification = DateTime.MinValue;
    private DateTime _lastUptimeNotification = DateTime.MinValue;
    private DateTime _lastDiskNotification = DateTime.MinValue;
    private static readonly TimeSpan NotificationCooldown = TimeSpan.FromHours(4);

    /// <summary>
    /// Whether the app should minimize to tray instead of closing.
    /// <para>No longer consulted when the window closes: that decision now comes from
    /// <see cref="ClosePreferenceService"/>, which asks the user once and persists the
    /// answer. This property never had any UI, so as a close switch it only ever produced
    /// its hardcoded default. Kept as the in-memory flag a future Settings tab can bind to
    /// (alongside <see cref="NotificationsEnabled"/>) rather than removed in a bug fix.</para>
    /// </summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Whether background notifications are enabled.</summary>
    public bool NotificationsEnabled { get; set; } = true;

    public TrayIconService(SystemInfoService sysInfo)
    {
        _sysInfo = sysInfo;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>
    /// Initializes the tray icon. Must be called from the UI thread.
    /// <para><paramref name="navigateToTab"/> is an optional callback the caller (the View layer,
    /// which legitimately knows the shell view-model) supplies to jump to a tab by nav id — used by every
    /// item in <see cref="QuickJumps"/>. Keeping it a callback rather than referencing the shell VM
    /// here preserves the Services→(not ViewModels) layering the architecture tests enforce.</para>
    /// <para>Supplying nothing is supported and omits those items rather than leaving them dead.</para>
    /// </summary>
    public void Initialize(Window mainWindow, Action<string>? navigateToTab = null)
    {
        // Track only an icon we own so Dispose can free its GDI handle. The shared
        // SystemIcons.Application fallback is process-wide and must not be disposed.
        _ownedIcon = LoadAppIcon();
        var icon = _ownedIcon ?? System.Drawing.SystemIcons.Application;
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "SysManager",
            Icon = icon,
            ContextMenu = BuildContextMenu(mainWindow, navigateToTab),
            Visibility = System.Windows.Visibility.Visible
        };
        _trayIcon.ForceCreate();

        _trayIcon.TrayLeftMouseDown += (_, _) => ShowWindow(mainWindow);

        _timer.Start();

        // Initial tooltip update
        _ = UpdateTooltipAsync();
    }

    /// <summary>
    /// Shows the main window and brings it to front.
    /// </summary>
    public static void ShowWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// Hides the main window to tray.
    /// </summary>
    public static void HideWindow(Window window)
    {
        window.Hide();
    }

    // ── Private ────────────────────────────────────────────────────────

    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            // First try: extract from current exe (works reliably in single-file publish)
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (extracted is not null) return extracted;
            }

            // Fallback: pack URI resource
            var uri = new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo?.Stream is null) return null;
            using var stream = streamInfo.Stream;
            return new System.Drawing.Icon(stream);
        }
        catch (Exception ex)
        {
            Log.Warning("TrayIcon: failed to load icon: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The tabs the tray offers a shortcut to, in menu order: nav id and the label the item shows.
    /// </summary>
    /// <remarks>
    /// Three, and the ceiling is deliberate — the tray is where this app spends most of its uptime, since
    /// minimize-to-tray is the default, so its menu is the primary interface for most of that time and it is
    /// also the easiest surface in the app to turn into clutter (#1589).
    /// <para><b>Every item is navigation only.</b> The CLI already implements <c>--cleanup</c> and
    /// <c>--trim-ram</c> headlessly, and it would have been one line to fire either from here. Neither is
    /// offered: a confirmation dialog is not visible from the tray, so a one-click <c>--cleanup</c> would
    /// delete files with nothing standing in front of it, which is exactly what the destructive-op rule
    /// exists to prevent.</para>
    /// <para><b>Process Manager sits directly under the CPU readout on purpose.</b> The header says the CPU
    /// is at 80%; the next question is what is using it. That ordering makes the menu a short diagnostic path
    /// rather than a list of links.</para>
    /// <para>Dashboard is deliberately NOT here. "Show SysManager" already opens whichever tab was last used,
    /// and Dashboard is the default, so an item for it would mostly duplicate the one above it.</para>
    /// </remarks>
    /// <remarks>
    /// <c>internal</c> so a test can assert the menu's jump items are built FROM this array rather than
    /// listing the same labels a second time. Raising Click on the items instead is not an option: the
    /// handler calls <c>ShowWindow</c>, and showing a window is exactly what a test must not do.
    /// </remarks>
    internal static readonly (string NavId, string Label)[] QuickJumps =
    [
        ("nav-processes", "What's using my PC"),
        ("nav-cleanup", "Free up space"),
        ("nav-volume-control", "Volume mixer"),
    ];

    /// <summary>
    /// Builds the tray context menu. <c>internal</c> so its structure can be asserted without creating a
    /// shell-registered tray icon: <see cref="Initialize"/> calls <c>ForceCreate</c>, which talks to the
    /// Windows notification area, while the menu itself is ordinary WPF and only needs an STA thread.
    /// </summary>
    internal System.Windows.Controls.ContextMenu BuildContextMenu(Window mainWindow, Action<string>? navigateToTab)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        // A disabled header, not a clickable item: it is a readout, and making it look pressable would
        // promise an action it does not have. Refreshed on Opened rather than per tick — see MenuStatusLine.
        _statusItem = new System.Windows.Controls.MenuItem { Header = MenuStatusLine, IsEnabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Opened += (_, _) =>
        {
            if (_statusItem is not null) _statusItem.Header = MenuStatusLine;
        };

        var showItem = new System.Windows.Controls.MenuItem { Header = "Show SysManager" };
        showItem.Click += (_, _) => ShowWindow(mainWindow);
        menu.Items.Add(showItem);

        // The quick jumps show the window and then navigate, via the caller-supplied callback (#332 asked
        // for the mixer; #1589 for the rest). Only added when a callback was provided, which keeps this
        // service VM-agnostic — and means a shell that wires no navigation gets the old two-item menu
        // rather than three dead entries.
        if (navigateToTab is not null)
        {
            foreach (var (navId, label) in QuickJumps)
            {
                var item = new System.Windows.Controls.MenuItem { Header = label };
                item.Click += (_, _) =>
                {
                    ShowWindow(mainWindow);
                    navigateToTab(navId);
                };
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) =>
        {
            App.RequestShutdown();
        };
        menu.Items.Add(exitItem);

        return menu;
    }

    /// <summary>
    /// The one-line readout the menu header shows: CPU, memory and uptime.
    /// </summary>
    /// <remarks>
    /// Separate from the tooltip's rendering rather than shared, because the two have different shapes for
    /// good reasons — the tooltip is three lines and capped at the 127 characters a taskbar tooltip allows,
    /// while a menu header is one line and has no cap. Both read the same snapshot, so they cannot disagree
    /// about the numbers, which is the part that would matter if they drifted.
    /// <para>Pure and <c>internal</c> so the wording is testable without a tray icon, a window or a timer.</para>
    /// </remarks>
    internal static string MenuStatusText(SystemSnapshot snapshot) =>
        string.Create(CultureInfo.InvariantCulture,
            $"CPU {snapshot.Cpu.LoadPercent:0}%  ·  RAM {snapshot.Memory.UsedGB:0.0}/{snapshot.Memory.TotalGB:0.0} GB  ·  up {snapshot.Os.Uptime.Days}d {snapshot.Os.Uptime.Hours}h");

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            await UpdateTooltipAsync();
        }
        catch (OperationCanceledException)
        {
            // Timer disposed during shutdown — safe to ignore.
        }
        catch (ObjectDisposedException)
        {
            // Tray icon disposed during shutdown — safe to ignore.
        }
        catch (InvalidOperationException ex)
        {
            // async void must never throw — unhandled exceptions crash the app.
            Log.Warning("TrayIcon timer tick failed: {Error}", ex.Message);
        }
        catch (Exception ex)
        {
            // Last-resort net: this is an async-void background timer, so ANY escaping
            // exception (e.g. an unexpected WMI/COM fault) would crash the process.
            // Swallow and log — a failed tooltip refresh must never take the app down.
            Log.Warning(ex, "TrayIcon timer tick failed unexpectedly");
        }
    }

    private async Task UpdateTooltipAsync()
    {
        // PERF-M4: Skip if a previous update is still running (WMI can be slow).
        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0)
            return;
        try
        {
            var snapshot = await _sysInfo.CaptureAsync();
            if (_trayIcon is null) return;

            var tooltip = $"SysManager\n" +
                          $"CPU: {snapshot.Cpu.LoadPercent:0}% | " +
                          string.Create(CultureInfo.InvariantCulture, $"RAM: {snapshot.Memory.UsedGB:0.0}/{snapshot.Memory.TotalGB:0.0} GB ({snapshot.Memory.UsedPercent:0}%)\n") +
                          $"Uptime: {snapshot.Os.Uptime.Days}d {snapshot.Os.Uptime.Hours}h";

            // TaskbarIcon tooltip max 127 chars
            _trayIcon.ToolTipText = tooltip.Length > 127 ? tooltip[..127] : tooltip;

            // Same snapshot, one-line rendering, for the context menu's header. Stored rather than pushed
            // into the item here: the menu reads it when it opens, so a menu that is never opened costs
            // nothing and one that is opened between ticks still shows the latest figures (#1589).
            MenuStatusLine = MenuStatusText(snapshot);

            // Check for notification conditions
            if (NotificationsEnabled)
                CheckAndNotify(snapshot);
        }
        catch (System.Management.ManagementException ex)
        {
            Log.Warning("TrayIcon tooltip update failed: {Error}", ex.Message);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // WMI commonly throws COMException on transient failures (RPC server
            // unavailable 0x800706BA, repository errors). The background timer must
            // never let this escape — OnTimerTick is async void, so an unhandled
            // throw would crash the whole app.
            Log.Warning("TrayIcon tooltip update failed (WMI COM error): 0x{HResult:X8}", ex.HResult);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("TrayIcon tooltip update failed: {Error}", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _updating, 0);
        }
    }

    internal void CheckAndNotify(SystemSnapshot snapshot)
    {
        var now = DateTime.UtcNow;

        // High RAM (>90%)
        if (snapshot.Memory.UsedPercent > 90 && now - _lastRamNotification > NotificationCooldown)
        {
            _lastRamNotification = now;
            ShowNotification("High Memory Usage",
                $"RAM is at {snapshot.Memory.UsedPercent:0}% — consider closing unused applications.");
        }

        // High uptime (>14 days)
        if (snapshot.Os.Uptime.TotalDays > 14 && now - _lastUptimeNotification > NotificationCooldown)
        {
            _lastUptimeNotification = now;
            ShowNotification("Restart Recommended",
                $"Your PC has been running for {(int)snapshot.Os.Uptime.TotalDays} days. A restart can improve performance.");
        }

        // Disk health warning.
        //
        // Matched against the values that MEAN a problem, rather than "anything that is not the word
        // Healthy". SystemInfoService.QueryDisks has two producer arms: the MSFT_PhysicalDisk arm maps
        // to "Healthy"/"Warning"/"Unhealthy"/"Unknown", but the Win32_DiskDrive FALLBACK passes
        // Win32_DiskDrive.Status straight through — and that reports "OK" for a perfectly good disk.
        // So on any machine that took the fallback, `!= "Healthy"` fired on every drive and the tray
        // popped "Disk Health Warning — reports status: OK", a toast that contradicts itself and tells
        // a non-technical user to back up over nothing. "Unknown" is excluded deliberately: not
        // knowing is not a failure, and it is what both arms emit when the value is unreadable.
        var unhealthyDisk = snapshot.Disks.FirstOrDefault(d => IsDiskProblem(d.HealthStatus));
        if (unhealthyDisk is not null && now - _lastDiskNotification > NotificationCooldown)
        {
            _lastDiskNotification = now;
            ShowNotification("Disk Health Warning",
                $"{unhealthyDisk.FriendlyName} reports status: {unhealthyDisk.HealthStatus}. Consider backing up important data.");
        }
    }

    /// <summary>
    /// True when a disk's reported status actually indicates trouble, across BOTH shapes
    /// <see cref="SystemInfoService"/> can produce for <c>DiskInfo.HealthStatus</c>: the
    /// MSFT_PhysicalDisk mapping ("Warning" / "Unhealthy") and the Win32_DiskDrive fallback, which
    /// passes <c>Win32_DiskDrive.Status</c> through raw ("OK", "Degraded", "Pred Fail", …).
    /// </summary>
    /// <remarks>
    /// <para>Keyed on the problem values rather than on "not Healthy" so a healthy disk described with
    /// any other wording — most importantly the fallback's "OK" — cannot raise a false alarm. Anything
    /// unrecognised, including "Unknown" and an empty value, is treated as NOT a problem: this drives
    /// an unprompted toast telling the user to back up, and inventing urgency from a value the app
    /// could not read is worse than staying quiet. The Disk Health tab still shows the raw status.</para>
    /// <para>The fallback's vocabulary is CIM's <c>Status</c> string set, which is ABBREVIATED to fit a
    /// 10-character field — "Pred Fail", "NonRecover", "Lost Comm" — and is NOT the long-form
    /// <c>OperationalStatus</c> wording ("Predictive Failure", "Non-Recoverable Error") that
    /// SystemInfoService's own <c>OpStatusName</c> map produces. Those long names only ever reach
    /// <c>DiskInfo.OperationalStatus</c>, a different property this method never sees, so matching them
    /// here would be dead code hiding the very failures it looks like it covers. Both vocabularies are
    /// accepted anyway: it costs nothing, and it keeps the predicate correct if a caller ever passes the
    /// operational status instead.</para>
    /// </remarks>
    internal static bool IsDiskProblem(string? status) => status?.Trim() switch
    {
        // MSFT_PhysicalDisk mapping (SystemInfoService's primary arm).
        "Warning" or "Unhealthy" => true,
        // Win32_DiskDrive.Status — CIM's abbreviated vocabulary, the fallback arm's actual values.
        "Degraded" or "Stressed" or "Pred Fail" or "Error" => true,
        "NonRecover" or "Lost Comm" or "No Contact" => true,
        // Long-form OperationalStatus wording, accepted for free in case a caller passes that instead.
        "Predictive Failure" or "Non-Recoverable Error" => true,
        _ => false,
    };

    private void ShowNotification(string title, string message)
    {
        try
        {
            _trayIcon?.ShowNotification(title, message, NotificationIcon.Warning);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("TrayIcon notification failed: {Error}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _trayIcon?.Dispose();
        _ownedIcon?.Dispose();
    }
}
