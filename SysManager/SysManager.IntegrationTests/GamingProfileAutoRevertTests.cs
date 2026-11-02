// SysManager · GamingProfileAutoRevertTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Gaming Profile's auto-revert when a real game exits: a child this test starts stands in for the game, and raising
/// that child's priority is the only change game mode makes.
/// </summary>
/// <remarks>
/// <see cref="IGamingProfileService.SessionAutoReverted"/> was documented as raised on the captured context, and
/// nothing captures one: it comes from the game's <c>Process.Exited</c> callback, on a thread-pool thread (#2745).
/// The one subscriber marshals to the UI thread itself. This pins the thread the event really arrives on, so the
/// contract a second subscriber reads is the one the code keeps. Only a real exit shows it: the thread is the one
/// .NET raises <c>Exited</c> on.
/// </remarks>
public sealed class GamingProfileAutoRevertTests
{
    [Fact]
    public async Task SessionAutoReverted_ArrivesOnAPoolThread_NotOnTheContextThatStartedGameMode()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var folder = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var child = await RunningChild.StartAsync(heldFile: null, bounded.Token);
            var runner = new RecordingRunner();
            using var service = new GamingProfileService(
                new PerformanceService(runner, new RestorePointService(runner), folder),
                new TimerResolutionService(),
                new CpuAffinityService(),
                new StandbyMemoryService(),
                // Never creates a point: a real System Restore call needs administrator rights and changes the PC.
                new SessionRestorePoint((_, _) => Task.FromResult(false)),
                isElevated: false,
                storePath: Path.Combine(folder, "gaming-profiles.json"));

            var raised = new TaskCompletionSource<(bool PoolThread, SynchronizationContext? Context)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.SessionAutoReverted += (_, _) =>
                raised.TrySetResult((Thread.CurrentThread.IsThreadPoolThread, SynchronizationContext.Current));

            // Game mode is started on a context of its own, which runs what is posted to it, so the event could
            // come back to it if anything captured it.
            var caller = new CallerContext();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(caller);
            Task<GamingApplyResult> applying;
            try
            {
                applying = service.ApplyAsync(
                    new GamingProfile { HighGameCpuPriority = true },
                    new GameTarget(child.Id, "the test's child", child.StartTime),
                    bounded.Token);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
            await applying.WaitAsync(bounded.Token);

            // The premise: the session is on and watching the child, so its exit is what ends it.
            Assert.True(service.IsActive, "game mode did not start, so no exit can end it");
            Assert.Equal(child.Id, service.BoundGamePid);

            child.Process.StandardInput.Close();
            var (poolThread, context) = await raised.Task.WaitAsync(bounded.Token);

            Assert.True(poolThread, "SessionAutoReverted arrived on a thread that is not a thread-pool thread");
            Assert.Null(context);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A context that runs what is posted to it on the pool, as itself, so a callback can tell it is here.</summary>
    private sealed class CallerContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => ThreadPool.QueueUserWorkItem(_ =>
        {
            SetSynchronizationContext(this);
            try
            {
                d(state);
            }
            finally
            {
                SetSynchronizationContext(null);
            }
        });

        public override SynchronizationContext CreateCopy() => this;
    }
}
