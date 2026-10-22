// SysManager · SingleInstanceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;

namespace SysManager.Tests;

/// <summary>
/// Tests for how a start finds the copy already running (#2799): the names of its mutex and pipe, the signal sent
/// through the pipe, and the loop that listens on it.
/// </summary>
/// <remarks>
/// One name for the whole machine let a copy running for another Windows account own the mutex and the pipe, and a
/// start that was refused either threw before logging existed, so SysManager never opened. The listener ended on the
/// first <see cref="IOException"/> it saw while running, so every later start did nothing. The pipe in the listener
/// tests is a script, so nothing here creates one; the integration suite refuses a real one.
/// </remarks>
public class SingleInstanceTests
{
    private const string OneAccount = "S-1-5-21-1000-2000-3000-1001";
    private const string AnotherAccount = "S-1-5-21-1000-2000-3000-1002";

    [Fact]
    public void TheMutex_IsPerSession_AndPerAccount()
    {
        Assert.StartsWith(@"Local\", App.MutexNameFor(OneAccount), StringComparison.Ordinal);
        Assert.NotEqual(App.MutexNameFor(OneAccount), App.MutexNameFor(AnotherAccount));
    }

    [Fact]
    public void ThePipe_IsPerAccount_AndPerSession()
    {
        // Pipe names are machine-wide, so the session has to be in the name, as it is in the mutex's namespace.
        var name = App.PipeNameFor(OneAccount, 1);

        Assert.DoesNotContain('\\', name);
        Assert.NotEqual(name, App.PipeNameFor(AnotherAccount, 1));
        Assert.NotEqual(name, App.PipeNameFor(OneAccount, 2));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]   // a copy this account may not reach (#2799)
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(IOException))]
    public void SignalThroughPipe_APipeThatDoesNotAnswer_IsFalse_NotAnException(Type refusal)
    {
        Assert.False(App.SignalThroughPipe(() => throw (Exception)Activator.CreateInstance(refusal)!));
    }

    [Fact]
    public void SignalThroughPipe_APipeThatAnswers_IsTrue()
    {
        Assert.True(App.SignalThroughPipe(() => { }));
    }

    // ── The listener ────────────────────────────────────────────────────────

    /// <summary>
    /// A pipe that answers each wait with the next of its turns: a connection, "The pipe is being closed" from a client
    /// that left before the wait, another failure, a cancel the listener did not ask for, and then a wait that holds
    /// until the listener is stopped.
    /// </summary>
    private sealed class ScriptedPipe(params string[] turns)
    {
        private int _waits;

        public int Waits => Volatile.Read(ref _waits);

        /// <summary>Set when the listener reaches the wait that holds.</summary>
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForOneAsync(CancellationToken ct)
        {
            var turn = Interlocked.Increment(ref _waits) - 1;
            return (turn < turns.Length ? turns[turn] : "hold") switch
            {
                "connect" => Task.CompletedTask,
                "closed" => Task.FromException(
                    new IOException("The pipe is being closed.", unchecked((int)0x800700E8))),
                "broken" => Task.FromException(new IOException("The pipe is broken.", unchecked((int)0x8007006D))),
                "cancelled elsewhere" => Task.FromCanceled(new CancellationToken(canceled: true)),
                _ => Hold(ct),
            };
        }

        private Task Hold(CancellationToken ct)
        {
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => stopped.TrySetCanceled(ct));
            Holding.TrySetResult();
            return stopped.Task;
        }
    }

    [Fact]
    public async Task TheListener_ActivatesForEachConnection_AndKeepsListening()
    {
        var pipe = new ScriptedPipe("connect", "connect");
        var activations = 0;
        using var stop = new CancellationTokenSource();

        var listening = App.ListenForActivationsAsync(
            pipe.WaitForOneAsync, () => Interlocked.Increment(ref activations), new HeldTimers(), stop.Token);
        await pipe.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, Volatile.Read(ref activations));
        Assert.False(listening.IsCompleted);
        await stop.CancelAsync();
        await listening.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(3, pipe.Waits);
    }

    [Fact]
    public async Task TheListener_APipeClosedBeforeTheWait_IsAConnection_AndListeningGoesOn()
    {
        // The client of a second start connected and closed before the wait began: it still asked for the window.
        var pipe = new ScriptedPipe("closed");
        var activations = 0;
        using var stop = new CancellationTokenSource();

        var listening = App.ListenForActivationsAsync(
            pipe.WaitForOneAsync, () => Interlocked.Increment(ref activations), new HeldTimers(), stop.Token);
        await pipe.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, Volatile.Read(ref activations));
        await stop.CancelAsync();
        await listening.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("cancelled elsewhere")]
    public async Task TheListener_AnotherFailure_IsWaitedOut_AndListeningGoesOn(string failure)
    {
        var pipe = new ScriptedPipe(failure);
        var activations = 0;
        var clock = new HeldTimers();
        using var stop = new CancellationTokenSource();

        var listening = App.ListenForActivationsAsync(
            pipe.WaitForOneAsync, () => Interlocked.Increment(ref activations), clock, stop.Token);

        // It pauses before the next wait, so a pipe that keeps failing cannot spin.
        Assert.Equal(App.ActivationRetryDelay, clock.OnlyDue);
        Assert.Equal(1, pipe.Waits);
        Assert.False(listening.IsCompleted);

        clock.FireAll();
        await pipe.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, pipe.Waits);
        Assert.Equal(0, Volatile.Read(ref activations));
        await stop.CancelAsync();
        await listening.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheListener_StoppedDuringItsPause_Ends()
    {
        var pipe = new ScriptedPipe("broken");
        using var stop = new CancellationTokenSource();

        var listening = App.ListenForActivationsAsync(pipe.WaitForOneAsync, () => { }, new HeldTimers(), stop.Token);
        Assert.False(listening.IsCompleted);

        await stop.CancelAsync();

        await listening.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, pipe.Waits);
    }
}
