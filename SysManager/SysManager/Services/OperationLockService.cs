// SysManager · OperationLockService — prevents conflicting concurrent operations
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Categories of operations that may conflict with each other.
/// Operations in the same category cannot run concurrently.
/// </summary>
public enum OperationCategory
{
    /// <summary>
    /// Disk-intensive: scanning or deleting across the disk. Temp Cleanup, Deep Cleanup's scan and clean, Disk
    /// Analyzer, Large Files, Duplicate Finder, Shortcut Cleaner, File Shredder, Browser Cleaner, and the
    /// Dashboard's Quick Cleanup and Quick Tune-Up.
    /// </summary>
    Disk,

    /// <summary>
    /// Network-intensive or network-changing: the speed tests, traceroute, Network Repair, and the DNS changes on
    /// DNS &amp; Hosts, so a DNS change never lands in the middle of a network-stack reset or a measurement.
    /// </summary>
    /// <remarks>
    /// Saving or restoring the hosts file takes no lock: nothing else in SysManager writes it, and each write swaps a
    /// finished copy into place. It took this one in #2510, which refused it for as long as a speed test ran (#2553).
    /// </remarks>
    Network,

    /// <summary>
    /// System modification: the repairs that service the running Windows image (SFC, DISM, the component-store
    /// cleanup), Windows feature changes, Windows Update installs and resets, creating or restoring a restore point,
    /// and the tabs that change Windows settings or services: Performance Mode, Gaming Profile, Environment Variables,
    /// Preinstalled Apps, Privacy &amp; Telemetry, Defender Tweaks, Edge/OneDrive Remover and Services.
    /// Deep Cleanup takes it too while it deletes one of its Windows Update caches.
    /// </summary>
    /// <remarks>
    /// Most of these service the same running Windows image, and a restore restarts it, so no two may overlap.
    /// Deep Cleanup's two caches sit inside SoftwareDistribution, which an update install reads from and Reset
    /// Windows Update renames (#2510). Windows Features, Windows Update, Reset Windows Update and the Restore Points
    /// tab took no lock until #2484.
    /// </remarks>
    SystemModification,

    /// <summary>
    /// Restarting the Windows shell: the Context Menu tab applying a menu style, and System Fixes
    /// repairing a frozen taskbar or rebuilding the icon cache.
    /// </summary>
    /// <remarks>
    /// Its own category rather than <see cref="SystemModification"/>, because the two must NOT exclude
    /// each other: an SFC scan runs for up to fifteen minutes, and a user whose taskbar froze during one
    /// still needs to be able to restart the shell. What must be exclusive is two shell restarts —
    /// overlapping ones can leave the user with no desktop at all, and now that two tabs can start one,
    /// a per-view-model IsBusy flag cannot prevent it.
    /// </remarks>
    Shell,

    /// <summary>
    /// Installing, upgrading or uninstalling software: App Updates, Bulk Installer, Uninstaller and the Dashboard's
    /// Update All Apps. Reset Windows Update takes it too, because it stops the Windows Installer service.
    /// </summary>
    /// <remarks>
    /// Windows Installer runs one installation at a time process-wide, so an MSI-based package started while
    /// another is mid-install, mid-upgrade or mid-uninstall can fail with exit code 1618 ("another installation
    /// is already in progress"), whichever of them started either one (#2510). Update All Apps runs the same winget
    /// upgrade and took no lock until #2553; Reset Windows Update, which stops the service outright, holds this lock
    /// besides <see cref="SystemModification"/> since then. Its own category rather than
    /// <see cref="SystemModification"/>: none of the installers touches Windows components, servicing or restore
    /// points, and gating an app upgrade behind an SFC scan or a Windows Update install would block ordinary app
    /// management for a resource the two do not share.
    /// </remarks>
    Install
}

/// <summary>
/// Thread-safe singleton that tracks running operations by category.
/// ViewModels acquire a lock before starting long operations and release it when done.
/// Conflicting operations (same category) are blocked while one is active.
/// </summary>
public sealed partial class OperationLockService : ObservableObject
{
    private static readonly Lazy<OperationLockService> _instance = new(() => new OperationLockService());
    public static OperationLockService Instance => _instance.Value;

    private readonly ConcurrentDictionary<OperationCategory, OperationInfo> _active = new();

    private OperationLockService() { }

    /// <summary>
    /// Attempts to acquire a lock for the given category.
    /// Returns a disposable handle if successful, or null if the category is already locked.
    /// </summary>
    /// <param name="category">The operation category to lock.</param>
    /// <param name="operationName">Human-readable name of the operation (for UI display).</param>
    /// <returns>A disposable lock handle, or null if the category is busy.</returns>
    public OperationHandle? TryAcquire(OperationCategory category, string operationName)
    {
        var info = new OperationInfo(operationName, DateTime.UtcNow);
        if (!_active.TryAdd(category, info))
            return null;

        try
        {
            OnPropertyChanged(nameof(ActiveOperations));
            OnPropertyChanged(nameof(HasActiveOperations));
        }
        catch
        {
            // A PropertyChanged subscriber threw. The entry is already in _active but no
            // handle has been returned, so the caller could never release it — that would
            // lock this category forever. Roll back the add and rethrow so the lock state
            // stays consistent.
            _active.TryRemove(category, out _);
            throw;
        }

        return new OperationHandle(this, category);
    }

    /// <summary>
    /// Checks whether a category is currently locked (an operation is running).
    /// </summary>
    public bool IsLocked(OperationCategory category) => _active.ContainsKey(category);

    /// <summary>
    /// Gets the name of the currently running operation in a category, or null if idle.
    /// </summary>
    public string? GetActiveOperationName(OperationCategory category)
        => _active.TryGetValue(category, out var info) ? info.Name : null;

    /// <summary>
    /// Gets a snapshot of all currently active operations.
    /// </summary>
    public IReadOnlyList<(OperationCategory Category, OperationInfo Info)> ActiveOperations
        => _active.Select(kv => (kv.Key, kv.Value)).ToList().AsReadOnly();

    /// <summary>
    /// Whether any operation is currently running.
    /// </summary>
    public bool HasActiveOperations => !_active.IsEmpty;

    private void Release(OperationCategory category)
    {
        _active.TryRemove(category, out _);
        OnPropertyChanged(nameof(ActiveOperations));
        OnPropertyChanged(nameof(HasActiveOperations));
    }

    /// <summary>
    /// Disposable handle returned by <see cref="TryAcquire"/>.
    /// Disposing it releases the operation lock.
    /// </summary>
    public sealed class OperationHandle : IDisposable
    {
        private readonly OperationLockService _service;
        private readonly OperationCategory _category;
        private int _disposed;

        internal OperationHandle(OperationLockService service, OperationCategory category)
        {
            _service = service;
            _category = category;
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
                _service.Release(_category);
        }
    }
}
