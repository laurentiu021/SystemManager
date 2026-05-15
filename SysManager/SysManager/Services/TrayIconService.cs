// SysManager · TrayIconService — system tray icon with background monitoring
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
    private bool _disposed;

    // Notification cooldowns — don't spam the user
    private DateTime _lastRamNotification = DateTime.MinValue;
    private DateTime _lastUptimeNotification = DateTime.MinValue;
    private DateTime _lastDiskNotification = DateTime.MinValue;
    private static readonly TimeSpan NotificationCooldown = TimeSpan.FromHours(4);

    /// <summary>Whether the app should minimize to tray instead of closing.</summary>
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
    /// </summary>
    public void Initialize(Window mainWindow)
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "SysManager — loading…",
            Icon = LoadAppIcon(),
            ContextMenu = BuildContextMenu(mainWindow)
        };

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
            var uri = new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo?.Stream == null) return null;
            using var stream = streamInfo.Stream;
            return new System.Drawing.Icon(stream);
        }
        catch (System.IO.IOException ex)
        {
            Log.Warning("TrayIcon: failed to load icon: {Error}", ex.Message);
            return null;
        }
    }

    private static System.Windows.Controls.ContextMenu BuildContextMenu(Window mainWindow)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var showItem = new System.Windows.Controls.MenuItem { Header = "Show SysManager" };
        showItem.Click += (_, _) => ShowWindow(mainWindow);
        menu.Items.Add(showItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) =>
        {
            Application.Current?.Shutdown();
        };
        menu.Items.Add(exitItem);

        return menu;
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            await UpdateTooltipAsync();
        }
        catch (Exception ex)
        {
            // async void must never throw — unhandled exceptions crash the app.
            Log.Warning("TrayIcon timer tick failed: {Error}", ex.Message);
        }
    }

    private async Task UpdateTooltipAsync()
    {
        try
        {
            var snapshot = await _sysInfo.CaptureAsync();
            if (_trayIcon == null) return;

            var tooltip = $"SysManager\n" +
                          $"CPU: {snapshot.Cpu.LoadPercent:0}% | " +
                          $"RAM: {snapshot.Memory.UsedGB:0.0}/{snapshot.Memory.TotalGB:0.0} GB ({snapshot.Memory.UsedPercent:0}%)\n" +
                          $"Uptime: {snapshot.Os.Uptime.Days}d {snapshot.Os.Uptime.Hours}h";

            // TaskbarIcon tooltip max 127 chars
            _trayIcon.ToolTipText = tooltip.Length > 127 ? tooltip[..127] : tooltip;

            // Check for notification conditions
            if (NotificationsEnabled)
                CheckAndNotify(snapshot);
        }
        catch (System.Management.ManagementException ex)
        {
            Log.Warning("TrayIcon tooltip update failed: {Error}", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("TrayIcon tooltip update failed: {Error}", ex.Message);
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

        // Disk health warning
        foreach (var disk in snapshot.Disks)
        {
            if (disk.HealthStatus != "Healthy" && now - _lastDiskNotification > NotificationCooldown)
            {
                _lastDiskNotification = now;
                ShowNotification("Disk Health Warning",
                    $"{disk.FriendlyName} reports status: {disk.HealthStatus}. Consider backing up important data.");
                break;
            }
        }
    }

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
    }
}
