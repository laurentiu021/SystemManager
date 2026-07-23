// SysManager · NotificationApp — model for a per-app notification sender
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// One application that has shown Windows toast notifications, backed by its
/// per-app subkey under <c>HKCU\...\Notifications\Settings</c>. <see cref="IsEnabled"/>
/// mirrors the same per-app switch Windows Settings exposes (true = notifications
/// allowed, which is also the default when the underlying value is absent).
/// </summary>
public sealed partial class NotificationApp : ObservableObject
{
    /// <summary>Whether Windows may show this app's notifications (the Settings per-app switch).</summary>
    [ObservableProperty] private bool _isEnabled;

    /// <summary>The Application User Model ID — the registry subkey name identifying the sender.</summary>
    public required string Aumid { get; init; }

    /// <summary>Human-readable name resolved from the AUMID registration (or prettified from the AUMID).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Windows' rolling count of notifications this app sent recently (read-only telemetry Windows keeps).</summary>
    public int RecentCount { get; init; }

    /// <summary>When this app last showed a notification, if Windows recorded it.</summary>
    public DateTime? LastNotification { get; init; }

    /// <summary>One-line activity caption for the UI row (recent count + last-seen time).</summary>
    public string ActivitySummary => LastNotification is { } t
        ? $"{RecentCount} recent · last {t:g}"
        : RecentCount > 0 ? $"{RecentCount} recent" : "no recent activity";
}
