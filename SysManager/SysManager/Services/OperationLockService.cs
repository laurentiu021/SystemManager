// SysManager · OperationLockService — prevents conflicting concurrent operations
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Services;

/// <summary>
/// Categories of operations that may conflict with each other.
/// Operations in the same category cannot run concurrently.
/// </summary>
public enum OperationCategory
{
    /// <summary>Disk-intensive: cleanup, disk analysis, duplicate scan, large file scan.</summary>
    Disk,

    /// <summary>Network-intensive: speed test, traceroute, ping flood, network repair.</summary>
    Network,

    /// <summary>System modification: performance tweaks, driver operations, Windows Update.</summary>
    SystemModification
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
    private readonly object _lock = new();

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
        lock (_lock)
        {
            if (_active.ContainsKey(category))
                return null;

            var info = new OperationInfo(operationName, DateTime.UtcNow);
            _active[category] = info;
            OnPropertyChanged(nameof(ActiveOperations));
            OnPropertyChanged(nameof(HasActiveOperations));
            return new OperationHandle(this, category);
        }
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
        lock (_lock)
        {
            _active.TryRemove(category, out _);
            OnPropertyChanged(nameof(ActiveOperations));
            OnPropertyChanged(nameof(HasActiveOperations));
        }
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

/// <summary>
/// Information about a currently running operation.
/// </summary>
public sealed record OperationInfo(string Name, DateTime StartedUtc);
