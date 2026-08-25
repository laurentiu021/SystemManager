// SysManager · SessionRestorePointTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the shared once-per-session restore point. The logic used to live privately inside
/// <c>TweaksHubService</c>, where nothing exercised it directly and no other tab could reach it; the
/// point of lifting it out was that every tab writing system state gets the same safety net, so the
/// semantics now need pinning rather than assuming.
/// <para>Driven through the injected create-delegate, so no test ever asks Windows for a real System
/// Restore point — that needs administrator, takes seconds, and is rate-limited to roughly one per 24
/// hours, which would make these tests slow, order-dependent, and machine-dependent. The delegate also
/// counts its own calls, which is what makes "once per session" observable.</para>
/// </summary>
public class SessionRestorePointTests
{
    /// <summary>A create-delegate that records how often it ran and what it was asked to name the point.</summary>
    private sealed class FakeCreator(bool result = true, Exception? throws = null)
    {
        public int Calls { get; private set; }
        public List<string> Descriptions { get; } = [];

        public Task<bool> CreateAsync(string description, CancellationToken ct)
        {
            Calls++;
            Descriptions.Add(description);
            return throws is not null ? Task.FromException<bool>(throws) : Task.FromResult(result);
        }
    }

    [Fact]
    public async Task EnsureAsync_FirstCall_CreatesThePointAndReportsIt()
    {
        var creator = new FakeCreator(result: true);
        var session = new SessionRestorePoint(creator.CreateAsync);

        Assert.True(await session.EnsureAsync("SysManager Test"));

        Assert.True(session.CreatedThisSession);
        Assert.Equal(1, creator.Calls);
        Assert.Equal("SysManager Test", Assert.Single(creator.Descriptions));
    }

    /// <summary>
    /// One attempt per session, not one per click. Windows refuses a second point within 24 hours
    /// anyway, and each attempt costs seconds of the user's time for nothing.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_SecondCall_DoesNotAskWindowsAgain()
    {
        var creator = new FakeCreator(result: true);
        var session = new SessionRestorePoint(creator.CreateAsync);

        await session.EnsureAsync("first");
        var second = await session.EnsureAsync("second");

        Assert.False(second);
        Assert.Equal(1, creator.Calls);
    }

    /// <summary>
    /// The split that keeps the UI honest: a later caller is told "I did not create one" — so it does
    /// not claim to have just taken a snapshot — while still being able to learn that the session HAS
    /// one. Reporting the per-call answer as the session fact is how a tab would end up saying "no
    /// restore point" moments after another tab made one.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_SecondCall_StillReportsThatTheSessionHasAPoint()
    {
        var session = new SessionRestorePoint(new FakeCreator(result: true).CreateAsync);

        await session.EnsureAsync("first");

        Assert.False(await session.EnsureAsync("second"));
        Assert.True(session.CreatedThisSession);
    }

    /// <summary>
    /// A "no" is the normal outcome on much of consumer Windows — System Restore is often off, needs
    /// administrator, and is rate-limited. It must surface as a plain false so no caller claims a
    /// safety net that does not exist.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_WhenWindowsDeclines_ReportsFalseAndNeverClaimsASession()
    {
        var session = new SessionRestorePoint(new FakeCreator(result: false).CreateAsync);

        Assert.False(await session.EnsureAsync("SysManager Test"));

        Assert.False(session.CreatedThisSession);
    }

    /// <summary>
    /// The failure the original code specifically swallowed: no administrator, or System Restore
    /// disabled by policy. It must not propagate — a tab whose Apply threw because the OPTIONAL
    /// snapshot failed would refuse the change the user actually asked for.
    /// </summary>
    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task EnsureAsync_WhenCreationThrows_IsSwallowedAndReportsFalse(Type exceptionType)
    {
        var creator = new FakeCreator(throws: (Exception)Activator.CreateInstance(exceptionType)!);
        var session = new SessionRestorePoint(creator.CreateAsync);

        Assert.False(await session.EnsureAsync("SysManager Test"));

        Assert.False(session.CreatedThisSession);
        Assert.Equal(1, creator.Calls);
    }

    /// <summary>
    /// A failed attempt still counts as the session's attempt. Retrying before every batch would make
    /// each Apply pay the timeout again on exactly the machines where it can never succeed.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_AfterAFailedAttempt_DoesNotRetry()
    {
        var creator = new FakeCreator(result: false);
        var session = new SessionRestorePoint(creator.CreateAsync);

        await session.EnsureAsync("first");
        await session.EnsureAsync("second");

        Assert.Equal(1, creator.Calls);
    }

    /// <summary>
    /// The attempt is claimed BEFORE the await, so two callers racing — a UI command and, say, a
    /// thread-pool callback — cannot both get past the check and ask Windows twice.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_ConcurrentCallers_StillAskWindowsOnce()
    {
        var creator = new FakeCreator(result: true);
        var session = new SessionRestorePoint(creator.CreateAsync);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => session.EnsureAsync($"caller {i}"))));

        Assert.Equal(1, creator.Calls);
        Assert.Single(results, created => created);
        Assert.True(session.CreatedThisSession);
    }
}
