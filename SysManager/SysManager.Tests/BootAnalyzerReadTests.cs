// SysManager · BootAnalyzerReadTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics.Eventing.Reader;
using SysManager.Services;
using static SysManager.Tests.ScriptedBootReader;

namespace SysManager.Tests;

/// <summary>
/// <see cref="BootAnalyzerService"/>'s read loop, driven through its reader seam (#2500).
/// </summary>
/// <remarks>
/// A failed read was retried with no bound, and the loop's counter only moved when an event came back, so an error
/// that repeats spun a thread until Cancel. A read that failed also came back as an empty list, which the tab
/// presented as "no boots recorded yet". These pin both: the loop gives up, and a failure is null, not empty.
/// </remarks>
public class BootAnalyzerReadTests
{
    private static BootAnalyzerService Over(ScriptedBootReader reader) => new(_ => reader);

    [Fact]
    public async Task WhenEveryReadFails_TheReadGivesUpAndReportsTheFailure()
    {
        var reader = new ScriptedBootReader(Fails());

        var boots = await Over(reader).ReadBootsAsync();

        Assert.Null(boots);
        Assert.Equal(BootAnalyzerService.MaxConsecutiveReadFailures, reader.Reads);
        Assert.True(reader.Disposed);
    }

    [Fact]
    public async Task OneFailedRead_IsRetried()
    {
        var reader = new ScriptedBootReader(Fails(), Returns(ScriptedBootEvents.Boot(42_000)), End());

        var boots = await Over(reader).ReadBootsAsync();

        Assert.NotNull(boots);
        Assert.Equal(42_000, Assert.Single(boots).BootTimeMs);
    }

    [Fact]
    public async Task FailuresSpreadOut_DoNotAddUp()
    {
        // The limit is on failures IN A ROW. Two isolated failures around a good read are two transient faults, not
        // an error that repeats, so the read carries on.
        var reader = new ScriptedBootReader(
            Fails(), Fails(), Returns(ScriptedBootEvents.Boot(30_000)),
            Fails(), Fails(), Returns(ScriptedBootEvents.Boot(31_000)), End());

        var boots = await Over(reader).ReadBootsAsync();

        Assert.NotNull(boots);
        Assert.Equal(2, boots.Count);
    }

    [Fact]
    public async Task FailingAfterSomeEvents_KeepsWhatWasRead()
    {
        var reader = new ScriptedBootReader(
            Returns(ScriptedBootEvents.Boot(40_000)), Returns(ScriptedBootEvents.Boot(41_000)), Fails());

        var boots = await Over(reader).ReadBootsAsync();

        Assert.NotNull(boots);
        Assert.Equal<long>([40_000, 41_000], boots.Select(b => b.BootTimeMs));
    }

    [Fact]
    public async Task AnEmptyLog_IsEmpty_NotAFailure()
    {
        var boots = await Over(new ScriptedBootReader(End())).ReadBootsAsync();

        Assert.NotNull(boots);
        Assert.Empty(boots);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("missing")]
    [InlineData("query")]
    public async Task ALogThatCannotBeOpened_IsAFailure_NotAnEmptyLog(string why)
    {
        Exception refusal = why switch
        {
            "denied" => new UnauthorizedAccessException("Attempted to perform an unauthorized operation."),
            "missing" => new EventLogNotFoundException("The specified channel could not be found."),
            _ => new EventLogException("The query is invalid."),
        };
        var service = new BootAnalyzerService(_ => throw refusal);

        Assert.Null(await service.ReadBootsAsync());
        Assert.Null(await service.ReadDegradationsAsync());
    }

    [Fact]
    public async Task SlowComponents_WhenEveryReadFails_AreAFailureToo()
    {
        var reader = new ScriptedBootReader(Fails());

        Assert.Null(await Over(reader).ReadDegradationsAsync());
        Assert.Equal(BootAnalyzerService.MaxConsecutiveReadFailures, reader.Reads);
    }

    [Fact]
    public async Task ReadStopsAtTheRequestedCount()
    {
        var reader = new ScriptedBootReader(Returns(ScriptedBootEvents.Boot(20_000)));

        var boots = await Over(reader).ReadBootsAsync(maxBoots: 5);

        Assert.NotNull(boots);
        Assert.Equal(5, boots.Count);
        Assert.Equal(5, reader.Reads);
    }

    [Fact]
    public async Task CancellingMidRead_Throws_RatherThanReturningAPartialHistory()
    {
        // It used to leave the loop quietly and return what it had, which the tab then showed as the full history.
        using var cts = new CancellationTokenSource();
        var reader = new ScriptedBootReader(
            () => { cts.Cancel(); return ScriptedBootEvents.Boot(50_000); },
            Returns(ScriptedBootEvents.Boot(51_000)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Over(reader).ReadBootsAsync(20, cts.Token));
        Assert.Equal(1, reader.Reads);
    }
}
