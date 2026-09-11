// SysManager · SynchronousProgress
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;

namespace SysManager.IntegrationTests;

/// <summary>
/// Collects <see cref="IProgress{T}"/> reports on the thread that raises them, for a test that asserts on
/// what a long-running operation reported.
/// </summary>
/// <remarks>
/// <b>NOT <see cref="Progress{T}"/>, which is what production uses.</b> That one POSTS each callback to a
/// captured synchronization context, so a report raised just before the awaited operation returns can still
/// be in flight when the assertions run — a test that passes on timing rather than on behaviour, and one that
/// would fail rarely and on a loaded machine. Implementing <see cref="IProgress{T}"/> directly makes
/// <c>Report</c> synchronous, so every report has arrived by the time the operation completes.
/// <para>A <see cref="ConcurrentQueue{T}"/> rather than a list: the reporting thread is not the test's
/// thread, and a queue's enumerator is a moment-in-time snapshot, so an enqueue arriving mid-read cannot
/// invalidate it. Enqueue order is preserved, so a test asserting a sequence keeps working. Same reasoning as
/// <c>PropertyChangeRecorder</c> in the unit project.</para>
/// <para>Shared because the second copy of it appeared within an hour of the first, in the test for the same
/// defect in the sibling scanner.</para>
/// </remarks>
public sealed class SynchronousProgress<T> : IProgress<T>
{
    /// <summary>Every report raised so far, in order.</summary>
    public ConcurrentQueue<T> Reports { get; } = new();

    /// <inheritdoc/>
    public void Report(T value) => Reports.Enqueue(value);
}
