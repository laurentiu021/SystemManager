// SysManager · PropertyChangeRecorder
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.ComponentModel;

namespace SysManager.Tests;

/// <summary>
/// Records the property names an <see cref="INotifyPropertyChanged"/> raises, into a collection that
/// stays safe to read while another thread is still raising them.
/// </summary>
/// <remarks>
/// The shape this replaces — <c>PropertyChanged += (_, e) =&gt; list.Add(e.PropertyName!)</c> — was
/// written about thirty times across the test projects, and whether each one was safe depended on
/// something no reader checked: whether that particular source had a second, off-thread writer. Most did
/// not. A view model whose constructor starts <c>InitializeAsync</c> does, because
/// <c>ViewModelBase</c> awaits it with <c>ConfigureAwait(false)</c> and the continuation resumes on the
/// thread pool — which is #2169, an <c>InvalidOperationException: Collection was modified; enumeration
/// operation may not execute</c> raised from inside an <c>Assert.Contains</c> on CI.
/// <para>Deciding the exposed subset by hand means reading thirty factories and re-reading them whenever
/// one changes. A recorder that is safe under a concurrent writer removes the question instead of
/// answering it thirty times, and costs nothing where there is no concurrent writer.</para>
/// <para><b><see cref="ConcurrentQueue{T}"/> rather than a lock around a list, and rather than a
/// snapshot.</b> <c>list.ToList()</c> enumerates too, and throws in exactly the same place. A queue's
/// enumerator is a moment-in-time snapshot, so an <c>Enqueue</c> arriving mid-read cannot invalidate it.
/// Enqueue order is preserved, so a test asserting a sequence of names keeps working.</para>
/// <para>The subscription is never removed. Every caller records a source it created for that one test
/// and drops both together, so there is nothing to leak — and taking an
/// <see cref="IDisposable"/> instead would put a <c>using</c> on all sixty call sites to solve a problem
/// none of them has.</para>
/// </remarks>
public static class PropertyChangeRecorder
{
    /// <summary>
    /// Subscribes to <paramref name="source"/> and returns the queue its raised property names land in.
    /// </summary>
    /// <remarks>
    /// <c>e.PropertyName</c> is forced non-null deliberately. The framework allows a null name, meaning
    /// "every property changed", but nothing in this app raises one, and a null in the queue would fail
    /// an assertion far from its cause. If a source ever does, this throws at the raise instead.
    /// </remarks>
    public static ConcurrentQueue<string> RecordPropertyChanges(this INotifyPropertyChanged source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var changed = new ConcurrentQueue<string>();
        source.PropertyChanged += (_, e) => changed.Enqueue(e.PropertyName!);
        return changed;
    }

    /// <summary>
    /// Subscribes to <paramref name="source"/> and returns the sequence of values <paramref name="read"/>
    /// produced each time <paramref name="propertyName"/> was raised.
    /// </summary>
    /// <remarks>
    /// The other overload records WHICH property changed; this records what it changed TO, which is what a
    /// busy-flag test needs: an operation that completes in microseconds cannot be sampled mid-flight, so
    /// "the bar went up and then came down" is only observable as <c>[true, false]</c> in the notifications
    /// themselves. Nine tests across seven tabs assert exactly that.
    /// <para>A <see cref="Func{T}"/> rather than reading <c>e</c>, because the event carries only the name:
    /// the value has to come from the source, at the moment it notified.</para>
    /// </remarks>
    public static ConcurrentQueue<T> RecordChangesOf<T>(
        this INotifyPropertyChanged source, string propertyName, Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentNullException.ThrowIfNull(read);

        var seen = new ConcurrentQueue<T>();
        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == propertyName) seen.Enqueue(read());
        };
        return seen;
    }
}
