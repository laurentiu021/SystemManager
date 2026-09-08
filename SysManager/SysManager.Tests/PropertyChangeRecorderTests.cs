// SysManager · PropertyChangeRecorderTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.ComponentModel;

namespace SysManager.Tests;

/// <summary>
/// Tests for the shared recorder the rest of the suite records notifications through.
/// </summary>
/// <remarks>
/// The one that matters is <see cref="RecordPropertyChanges_SurvivesAWriterOnAnotherThread"/>. Every other
/// test here would pass just as happily against the plain <c>List&lt;string&gt;</c> this replaced, which is
/// exactly why sixty-one hand-rolled copies of that list looked fine for as long as they did.
/// </remarks>
public class PropertyChangeRecorderTests
{
    /// <summary>Minimal notifier: this tests the recorder, not any particular view model.</summary>
    private sealed class Notifier : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public int Value { get; private set; }

        public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void SetValue(int value, string name)
        {
            Value = value;
            Raise(name);
        }
    }

    [Fact]
    public void RecordPropertyChanges_CapturesEveryNameInOrder()
    {
        var source = new Notifier();
        var changed = source.RecordPropertyChanges();

        source.Raise("First");
        source.Raise("Second");
        source.Raise("First");

        Assert.Equal(["First", "Second", "First"], changed);
    }

    [Fact]
    public void RecordPropertyChanges_BeforeAnythingIsRaised_IsEmpty()
    {
        Assert.Empty(new Notifier().RecordPropertyChanges());
    }

    /// <summary>
    /// The whole point: reading the record while the source is still notifying must not throw.
    /// </summary>
    /// <remarks>
    /// This is the failure #2169 hit on CI — <c>InvalidOperationException: Collection was modified;
    /// enumeration operation may not execute</c> from inside an <c>Assert.Contains</c>, once in about five
    /// thousand tests, because a view model's <c>InitializeAsync</c> continuation was still raising
    /// notifications on the thread pool while the assertion enumerated a plain <c>List</c>.
    /// <para>Deterministic in the direction that counts: a <see cref="ConcurrentQueue{T}"/> enumerator is a
    /// moment-in-time snapshot, so this CANNOT throw for a correct implementation however the two threads
    /// interleave — there is no flake to inherit. Against a plain <c>List</c> the same loop throws nearly
    /// every time, which is what makes it a real test rather than a restatement of the signature. No sleep
    /// and no wall clock: the reader spins until the writer's fixed count is done.</para>
    /// </remarks>
    [Fact]
    public async Task RecordPropertyChanges_SurvivesAWriterOnAnotherThread()
    {
        const int raises = 20_000;
        var source = new Notifier();
        var changed = source.RecordPropertyChanges();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < raises; i++) source.Raise("Churn" + i);
        });

        var reads = 0;
        while (!writer.IsCompleted)
        {
            // Enumerating is the operation that throws on a List being appended to concurrently.
            foreach (var _ in changed) { }
            reads++;
        }

        await writer;

        Assert.True(reads > 0, "the writer finished before a single read — this proved nothing");
        Assert.Equal(raises, changed.Count);
    }

    [Fact]
    public void RecordChangesOf_CapturesTheValueAtEachNotification()
    {
        var source = new Notifier();
        var seen = source.RecordChangesOf(nameof(Notifier.Value), () => source.Value);

        source.SetValue(1, nameof(Notifier.Value));
        source.SetValue(2, nameof(Notifier.Value));

        Assert.Equal([1, 2], seen);
    }

    [Fact]
    public void RecordChangesOf_IgnoresEveryOtherProperty()
    {
        var source = new Notifier();
        var seen = source.RecordChangesOf(nameof(Notifier.Value), () => source.Value);

        source.SetValue(7, "SomethingElse");

        Assert.Empty(seen);
    }

    [Fact]
    public void RecordPropertyChanges_NullSource_Throws()
    {
        INotifyPropertyChanged? source = null;
        Assert.Throws<ArgumentNullException>(() => source!.RecordPropertyChanges());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordChangesOf_WithoutAPropertyName_Throws(string? name)
    {
        var source = new Notifier();
        Assert.ThrowsAny<ArgumentException>(() => source.RecordChangesOf(name!, () => source.Value));
    }

    [Fact]
    public void RecordChangesOf_WithoutAReader_Throws()
    {
        var source = new Notifier();
        Assert.Throws<ArgumentNullException>(
            () => source.RecordChangesOf<int>(nameof(Notifier.Value), null!));
    }
}
