// SysManager · SyncProgress
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Tests;

/// <summary>
/// Synchronous <see cref="IProgress{T}"/> that records every report on the calling
/// thread. Unlike <see cref="System.Progress{T}"/> — which marshals callbacks via the
/// captured <see cref="System.Threading.SynchronizationContext"/> asynchronously — this
/// captures each report deterministically by the time the awaited call returns, so tests
/// need no <c>Task.Delay</c> guess and never race the callback pump.
/// </summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T>? _onReport;

    public SyncProgress() { }

    /// <summary>Also invoke <paramref name="onReport"/> synchronously per report —
    /// e.g. to cancel a token mid-operation in a deterministic test.</summary>
    public SyncProgress(Action<T> onReport) => _onReport = onReport;

    public List<T> Reports { get; } = [];
    public void Report(T value) { Reports.Add(value); _onReport?.Invoke(value); }
}
