// SysManager · EventLogServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics.Eventing.Reader;
using System.Reflection;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="EventLogService"/> — focuses on the pure-logic
/// BuildXPath and MapLevel methods that don't require the Windows Event Log.
/// </summary>
public class EventLogServiceTests
{
    // ---------- BuildXPath ----------

    private static string InvokeBuildXPath(EventLogQueryOptions opt)
    {
        var m = typeof(EventLogService).GetMethod("BuildXPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, new object[] { opt })!;
    }

    [Fact]
    public void BuildXPath_NoFilters_ReturnsStar()
    {
        var result = InvokeBuildXPath(new EventLogQueryOptions());
        Assert.Equal("*", result);
    }

    // BuildXPath_WithSeverity_IncludesLevel and BuildXPath_MultipleSeverities_IncludesOr used to sit here,
    // asserting the query carried a Level clause. That clause is gone — it made a single ReadEvent() block
    // uninterruptibly while the OS searched for a match — so both assertions are now inverted, and
    // BuildXPath_NeverFiltersSeverityInTheQuery below asserts the absence in their place. What they were
    // really about, that one or several requested severities are honoured, is covered by the Matches tests
    // against the code that now does the filtering.

    [Fact]
    public void BuildXPath_WithSince_IncludesTimeCreated()
    {
        var opt = new EventLogQueryOptions
        {
            Since = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc)
        };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("TimeCreated", result);
        Assert.Contains("2026-01-15", result);
    }

    [Fact]
    public void BuildXPath_Since_UsesInvariantTimeSeparator_OnDotSeparatorCulture()
    {
        // Regression (F16): the SystemTime timestamp was formatted without a culture, so on a
        // locale whose TimeSeparator is '.' (e.g. fi-FI) the ':' in the format became '.',
        // producing an invalid SystemTime like "10.30.00" — EventLogQuery then threw and the
        // Logs tab came back empty. Force such a culture and assert the ISO ':' survives.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("fi-FI");

            var opt = new EventLogQueryOptions
            {
                Since = new DateTime(2026, 1, 15, 10, 30, 45, DateTimeKind.Utc)
            };
            var result = InvokeBuildXPath(opt);

            // Valid ISO 8601 time uses ':' regardless of the OS display language.
            Assert.Contains("T10:30:45", result);
            Assert.DoesNotContain("10.30.45", result);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void BuildXPath_WithProvider_IncludesProviderName()
    {
        var opt = new EventLogQueryOptions { ProviderName = "disk" };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("Provider[@Name='disk']", result);
    }

    [Fact]
    public void BuildXPath_WithEventId_IncludesEventID()
    {
        var opt = new EventLogQueryOptions { EventId = 7 };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("EventID=7", result);
    }

    [Fact]
    public void BuildXPath_AllFilters_CombinesWithAnd()
    {
        var opt = new EventLogQueryOptions
        {
            Severities = new List<EventSeverity> { EventSeverity.Critical },
            Since = DateTime.UtcNow.AddDays(-7),
            ProviderName = "disk",
            EventId = 11
        };
        var result = InvokeBuildXPath(opt);
        Assert.Contains(" and ", result);
        Assert.Contains("Provider[@Name='disk']", result);
        Assert.Contains("EventID=11", result);
        // Severities is set above and contributes nothing, which is the point: the clauses the OS can bound
        // cheaply still combine, and severity is not one of them.
        Assert.DoesNotContain("Level", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildXPath_ProviderWithMetacharacters_IsRejectedNotMangled()
    {
        // idx 214: a provider name containing XPath metacharacters is now REJECTED via
        // an allowlist (the clause is dropped) rather than silently stripped into a
        // different name. Injection is still impossible AND we never build a wrong filter.
        var opt = new EventLogQueryOptions { ProviderName = "test'injection" };
        var result = InvokeBuildXPath(opt);
        Assert.DoesNotContain("'injection", result);   // no injection
        Assert.DoesNotContain("Provider", result);      // clause dropped, not mangled-in
        Assert.DoesNotContain("testinjection", result); // not silently rewritten
    }

    [Fact]
    public void BuildXPath_ProviderWithSpacesAndDots_IsAccepted()
    {
        // Real provider names like "Microsoft-Windows-Kernel-Power" or "Service Control
        // Manager" must pass the allowlist verbatim.
        var opt = new EventLogQueryOptions { ProviderName = "Microsoft-Windows-Kernel-Power" };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("Provider[@Name='Microsoft-Windows-Kernel-Power']", result);
    }

    // ---------- MapLevel ----------

    private static EventSeverity InvokeMapLevel(byte? level)
    {
        var m = typeof(EventLogService).GetMethod("MapLevel", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (EventSeverity)m.Invoke(null, new object?[] { level })!;
    }

    [Theory]
    [InlineData((byte)1, EventSeverity.Critical)]
    [InlineData((byte)2, EventSeverity.Error)]
    [InlineData((byte)3, EventSeverity.Warning)]
    [InlineData((byte)4, EventSeverity.Info)]
    [InlineData((byte)5, EventSeverity.Verbose)]
    public void MapLevel_KnownLevels_ReturnCorrectSeverity(byte level, EventSeverity expected)
        => Assert.Equal(expected, InvokeMapLevel(level));

    [Fact]
    public void MapLevel_Null_ReturnsInfo()
        => Assert.Equal(EventSeverity.Info, InvokeMapLevel(null));

    [Fact]
    public void MapLevel_UnknownValue_ReturnsInfo()
        => Assert.Equal(EventSeverity.Info, InvokeMapLevel((byte)99));

    // ---------- Matches: the severity filter, now applied in managed code ----------

    /// <summary>
    /// A requested severity admits every raw Level that <c>MapLevel</c> folds into it.
    /// </summary>
    /// <remarks>
    /// This is the invariant <c>SeverityToLevels</c> used to carry. That method existed only to build the
    /// query's <c>Level</c> clause, and the clause is gone: it made a single <c>ReadEvent()</c> block
    /// uninterruptibly for minutes while the OS searched for a match, which is the cancellation defect this
    /// change fixes. The method went with it rather than staying alive on the strength of its own test.
    /// <para>Info is the case that matters and the reason its predecessor was once wrong. <c>MapLevel</c>
    /// folds Level 0 (LogAlways) into Info, so an Info filter must admit BOTH 0 and 4 or every LogAlways
    /// event silently disappears from an Info-filtered view. A dead twin once encoded <c>Info => 4</c> and a
    /// reflection test certified it.</para>
    /// </remarks>
    [Theory]
    [InlineData(EventSeverity.Critical, (byte)1)]
    [InlineData(EventSeverity.Error, (byte)2)]
    [InlineData(EventSeverity.Warning, (byte)3)]
    [InlineData(EventSeverity.Verbose, (byte)5)]
    [InlineData(EventSeverity.Info, (byte)4)]
    [InlineData(EventSeverity.Info, (byte)0)]
    public void Matches_AdmitsEveryLevelThatMapsIntoTheRequestedSeverity(EventSeverity severity, byte level)
        => Assert.True(EventLogService.Matches([severity], level));

    /// <summary>A severity that was not asked for is rejected, including the LogAlways edge.</summary>
    /// <remarks>
    /// The negative half of the pair above. Without it, a filter that admitted everything would satisfy
    /// every row of that theory — <c>Level 0</c> against a Critical-only filter is the specific case the old
    /// <c>BuildXPath_CriticalOnly_DoesNotIncludeLevel0</c> guarded.
    /// </remarks>
    [Theory]
    [InlineData(EventSeverity.Critical, (byte)0)]
    [InlineData(EventSeverity.Critical, (byte)2)]
    [InlineData(EventSeverity.Error, (byte)3)]
    [InlineData(EventSeverity.Info, (byte)1)]
    public void Matches_RejectsALevelThatWasNotAskedFor(EventSeverity severity, byte level)
        => Assert.False(EventLogService.Matches([severity], level));

    [Fact]
    public void Matches_MultipleSeverities_AdmitsEachOfThem()
    {
        List<EventSeverity> both = [EventSeverity.Info, EventSeverity.Error];

        Assert.True(EventLogService.Matches(both, 0));
        Assert.True(EventLogService.Matches(both, 4));
        Assert.True(EventLogService.Matches(both, 2));
        Assert.False(EventLogService.Matches(both, 3));
    }

    /// <summary>No filter admits everything, including a record with no Level at all.</summary>
    /// <remarks>
    /// The Logs tab's default is an unfiltered read, so this is the path most users take. A null Level folds
    /// to Info the same way Level 0 does, and must not be dropped by an absent filter.
    /// </remarks>
    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)5)]
    [InlineData(null)]
    public void Matches_NoFilter_AdmitsEverything(byte? level)
    {
        Assert.True(EventLogService.Matches(null, level));
        Assert.True(EventLogService.Matches([], level));
    }

    /// <summary>
    /// The query carries no <c>Level</c> clause, whatever severities were asked for.
    /// </summary>
    /// <remarks>
    /// The clause is what made one <c>ReadEvent()</c> block for 4 m 43 s against a 10-second token on a CI
    /// runner: the Event Log service evaluates the query itself and returns only once it finds a match, and
    /// that native call cannot be cancelled. Asserting its ABSENCE is the only thing that stops it being
    /// reinstated later as an obvious-looking optimisation — the filter would still work, and only the
    /// cancellation would quietly break again.
    /// </remarks>
    [Fact]
    public void BuildXPath_NeverFiltersSeverityInTheQuery()
    {
        var opt = new EventLogQueryOptions
        {
            Severities = [EventSeverity.Info, EventSeverity.Error, EventSeverity.Critical]
        };

        var result = InvokeBuildXPath(opt);

        Assert.DoesNotContain("Level", result, StringComparison.Ordinal);
        // Still "*" here, because severity was the only filter asked for — proving the clause was dropped
        // rather than merely renamed.
        Assert.Equal("*", result);
    }

    /// <summary>The other clauses are untouched by that removal.</summary>
    [Fact]
    public void BuildXPath_StillFiltersTheThingsTheOsCanBoundCheaply()
    {
        var opt = new EventLogQueryOptions
        {
            Severities = [EventSeverity.Error],
            Since = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        };

        var result = InvokeBuildXPath(opt);

        Assert.Contains("TimeCreated", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Level", result, StringComparison.Ordinal);
    }

    // ---------- EventLogQueryOptions defaults ----------

    [Fact]
    public void QueryOptions_DefaultLogName_IsSystem()
    {
        var opt = new EventLogQueryOptions();
        Assert.Equal("System", opt.LogName);
    }

    [Fact]
    public void QueryOptions_DefaultMaxResults_Is500()
    {
        var opt = new EventLogQueryOptions();
        Assert.Equal(500, opt.MaxResults);
    }

    [Fact]
    public void QueryOptions_DefaultSeverities_IsNull()
    {
        var opt = new EventLogQueryOptions();
        Assert.Null(opt.Severities);
    }

    // ---------- ReadOutcome ----------
    //
    // The reader used to swallow UnauthorizedAccessException with a bare `yield break`, so a
    // refused log and an empty log were indistinguishable — a standard user selecting Security
    // saw "Loaded 0 events" over a blank grid. LastOutcome carries the reason so the UI can
    // say which it was.

    [Fact]
    public async Task Read_NonexistentLog_ReportsLogNotFound()
    {
        var svc = new EventLogService();
        var opt = new EventLogQueryOptions { LogName = "SysManagerNoSuchLog", MaxResults = 5 };

        var count = 0;
        await foreach (var _ in svc.ReadAsync(opt, CancellationToken.None)) count++;

        Assert.Equal(0, count);
        Assert.Equal(EventLogService.ReadOutcome.LogNotFound, svc.LastOutcome);
    }

    [Fact]
    public void ReadOutcome_DefaultIsOk()
    {
        // Before any query, Ok is the honest state: nothing has been refused. It also means a
        // caller reading LastOutcome without querying cannot observe a spurious failure.
        Assert.Equal(EventLogService.ReadOutcome.Ok, new EventLogService().LastOutcome);
        Assert.Equal(EventLogService.ReadOutcome.Ok, default(EventLogService.ReadOutcome));
    }

    [Fact]
    public async Task Read_OutcomeIsResetAtTheStartOfEachQuery()
    {
        // A failed query must not leave the flag set for the next one, or the UI would keep the
        // refusal overlay up over a successful reload. Both queries here name a log that does
        // not exist, so the assertion is about the reset being unconditional rather than about
        // any particular machine's event logs — reading a real log would make this depend on
        // the runner's log contents and permissions.
        var svc = new EventLogService();

        await foreach (var _ in svc.ReadAsync(
            new EventLogQueryOptions { LogName = "SysManagerNoSuchLog" }, CancellationToken.None)) { }
        Assert.Equal(EventLogService.ReadOutcome.LogNotFound, svc.LastOutcome);

        // Reaching the open step again re-evaluates the outcome rather than keeping the old one.
        await foreach (var _ in svc.ReadAsync(
            new EventLogQueryOptions { LogName = "SysManagerAlsoMissing" }, CancellationToken.None)) { }
        Assert.Equal(EventLogService.ReadOutcome.LogNotFound, svc.LastOutcome);
    }
    // ---------- the read loop: a fault must end it, a cancel must surface as one ----------

    private static EventLogQueryOptions Options(int maxResults = 500) =>
        new() { LogName = "System", MaxResults = maxResults };

    [Fact]
    public async Task Enumerate_ReaderAlwaysFaults_EndsInsteadOfSpinning()
    {
        // The P1. The loop's only progress variable is the emitted count, which a failure never increments,
        // so `catch (EventLogException) { continue; }` spun with no delay — one Task.Run per iteration, one
        // core at 100%, the tab stuck loading with Refresh disabled because it is gated on not-busy. This
        // test simply cannot finish against that code.
        var svc = new EventLogService();
        var reads = 0;

        var entries = new List<FriendlyEventEntry>();
        await foreach (var e in svc.Enumerate(Options(), () =>
        {
            reads++;
            throw new EventLogException("reader is stale");
        }, CancellationToken.None))
        {
            entries.Add(e);
        }

        Assert.Empty(entries);
        // Ended on the first fault rather than retrying: EvtNext does not advance its cursor on these
        // failures, so a retry re-throws the identical error and a budget would only postpone the exit.
        Assert.Equal(1, reads);
        Assert.Equal(EventLogService.ReadOutcome.Unavailable, svc.LastOutcome);
    }

    [Fact]
    public async Task Enumerate_CancelledMidRead_ThrowsInsteadOfCompleting()
    {
        // The P2. `catch (OperationCanceledException) { yield break; }` made Cancel look like a finished
        // load, so the caller reported "Loaded N events" for a truncated list and LogsViewModel's cancel
        // branch was dead code. Two sibling scanners already throw after their loop for this exact reason.
        var svc = new EventLogService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in svc.Enumerate(Options(), () => null, cts.Token))
            {
                // The loop body never runs on an already-cancelled token; the throw is the assertion.
            }
        });
    }

    [Fact]
    public async Task Enumerate_ReaderReturnsNull_CompletesCleanly()
    {
        // The ordinary end-of-results path, so the fault handling above cannot be satisfied by a loop that
        // simply always stops. A null record means the result set is exhausted and the outcome stays Ok.
        var svc = new EventLogService();

        var entries = new List<FriendlyEventEntry>();
        await foreach (var e in svc.Enumerate(Options(), () => null, CancellationToken.None))
            entries.Add(e);

        Assert.Empty(entries);
        Assert.Equal(EventLogService.ReadOutcome.Ok, svc.LastOutcome);
    }

    [Fact]
    public async Task GetXmlAsync_UnknownRecordId_ReturnsEmptyRatherThanThrowing()
    {
        // An event log is a ring buffer, so a row visible in the list can genuinely have rolled off by the
        // time it is clicked. That is not an error worth surfacing — the detail pane just shows nothing.
        var svc = new EventLogService();

        var xml = await svc.GetXmlAsync("System", long.MaxValue);

        Assert.Equal("", xml);
    }

    [Fact]
    public async Task GetXmlAsync_NonPositiveRecordId_ShortCircuits()
    {
        // Project leaves RecordId at 0 when the record carries none, and querying EventRecordID=0 is a
        // pointless round-trip.
        var svc = new EventLogService();

        Assert.Equal("", await svc.GetXmlAsync("System", 0));
        Assert.Equal("", await svc.GetXmlAsync("System", -1));
    }
    [Fact]
    public async Task Enumerate_ReadRaisesCancellation_NeverLooksLikeCompletion()
    {
        // The one route where swallowing OperationCanceledException inside the loop is observable: the read
        // raises it while the token itself is NOT cancelled, so the post-loop guard has nothing to fire on.
        // `catch (OperationCanceledException) { yield break; }` turned that into a clean finish, which is
        // exactly the conversion LargeFileScanner and DuplicateFileService both throw to avoid.
        var svc = new EventLogService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in svc.Enumerate(
                Options(), () => throw new OperationCanceledException(), CancellationToken.None))
            {
                // Reaching the body would mean the read succeeded; the throw is the assertion.
            }
        });
    }
}
