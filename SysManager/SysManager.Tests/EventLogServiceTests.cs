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

    [Fact]
    public void BuildXPath_WithSeverity_IncludesLevel()
    {
        var opt = new EventLogQueryOptions
        {
            Severities = [EventSeverity.Error]
        };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("Level=2", result);
    }

    [Fact]
    public void BuildXPath_MultipleSeverities_IncludesOr()
    {
        var opt = new EventLogQueryOptions
        {
            Severities = new List<EventSeverity> { EventSeverity.Error, EventSeverity.Warning }
        };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("Level=2", result);
        Assert.Contains("Level=3", result);
        Assert.Contains(" or ", result);
    }

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
        Assert.Contains("Level=1", result);
        Assert.Contains("Provider[@Name='disk']", result);
        Assert.Contains("EventID=11", result);
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

    // ---------- SeverityToLevels (the live inverse of MapLevel) ----------

    [Theory]
    [InlineData(EventSeverity.Critical, new[] { 1 })]
    [InlineData(EventSeverity.Error, new[] { 2 })]
    [InlineData(EventSeverity.Warning, new[] { 3 })]
    [InlineData(EventSeverity.Verbose, new[] { 5 })]
    // Info is the case that matters and the reason the old test was wrong. MapLevel folds Level 0
    // (LogAlways) into Info, so the XPath must ask for BOTH 0 and 4 or every LogAlways event silently
    // disappears from an Info-filtered view. A dead twin of this method encoded `Info => 4` and a reflection
    // test certified it; both are gone.
    [InlineData(EventSeverity.Info, new[] { 0, 4 })]
    public void SeverityToLevels_IsTheExactInverseOfMapLevel(EventSeverity severity, int[] expected)
        => Assert.Equal(expected, EventLogService.SeverityToLevels(severity));


    // ---------- P2 #32 regression: Info filter must include Level 0 (LogAlways) ----------

    [Fact]
    public void BuildXPath_InfoSeverity_IncludesLevel0AndLevel4()
    {
        var opt = new EventLogQueryOptions
        {
            Severities = [EventSeverity.Info]
        };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("Level=0", result);
        Assert.Contains("Level=4", result);
        Assert.Contains(" or ", result);
    }

    [Fact]
    public void BuildXPath_InfoAndError_IncludesLevel0_Level4_Level2()
    {
        var opt = new EventLogQueryOptions
        {
            Severities = [EventSeverity.Info, EventSeverity.Error]
        };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("Level=0", result);
        Assert.Contains("Level=4", result);
        Assert.Contains("Level=2", result);
    }

    [Fact]
    public void BuildXPath_CriticalOnly_DoesNotIncludeLevel0()
    {
        var opt = new EventLogQueryOptions
        {
            Severities = [EventSeverity.Critical]
        };
        var result = InvokeBuildXPath(opt);
        Assert.Contains("Level=1", result);
        Assert.DoesNotContain("Level=0", result);
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
