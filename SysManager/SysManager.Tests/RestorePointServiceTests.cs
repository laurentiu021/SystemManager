// SysManager · RestorePointServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management.Automation;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="RestorePointService.ParseRestorePoints"/> — the pure parser that
/// turns <c>Get-ComputerRestorePoint</c> output into <see cref="RestorePoint"/> records.
/// Building PSObjects directly keeps these deterministic and free of any live PowerShell
/// or System Restore dependency. The create/restore paths hit the OS and are not unit-tested.
/// </summary>
public class RestorePointServiceTests
{
    private static PSObject MakeRow(object? seq, string? desc, string? iso, string? type, string? evt)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("SequenceNumber", seq));
        o.Properties.Add(new PSNoteProperty("Description", desc));
        o.Properties.Add(new PSNoteProperty("CreationTimeIso", iso));
        o.Properties.Add(new PSNoteProperty("RestorePointType", type));
        o.Properties.Add(new PSNoteProperty("EventType", evt));
        return o;
    }

    [Fact]
    public void Parse_MapsAllFields()
    {
        var rows = new[] { MakeRow(7, "Before update", "2026-01-02T13:14:15.0000000+00:00", "MODIFY_SETTINGS", "BEGIN_SYSTEM_CHANGE") };
        var result = RestorePointService.ParseRestorePoints(rows);

        Assert.Single(result);
        var rp = result[0];
        Assert.Equal(7, rp.SequenceNumber);
        Assert.Equal("Before update", rp.Description);
        Assert.Equal("MODIFY_SETTINGS", rp.RestorePointType);
        Assert.Equal("BEGIN_SYSTEM_CHANGE", rp.EventType);
        Assert.Equal(2026, rp.CreationTime.Year);
    }

    [Fact]
    public void Parse_SortsNewestFirstBySequence()
    {
        var rows = new[]
        {
            MakeRow(3, "c", "2026-01-03T00:00:00Z", "MODIFY_SETTINGS", "x"),
            MakeRow(1, "a", "2026-01-01T00:00:00Z", "MODIFY_SETTINGS", "x"),
            MakeRow(2, "b", "2026-01-02T00:00:00Z", "MODIFY_SETTINGS", "x"),
        };
        var result = RestorePointService.ParseRestorePoints(rows);
        Assert.Equal([3, 2, 1], result.Select(r => r.SequenceNumber));
    }

    [Fact]
    public void Parse_SkipsRowsWithoutSequenceNumber()
    {
        var rows = new[]
        {
            MakeRow(null, "no seq", "2026-01-01T00:00:00Z", "MODIFY_SETTINGS", "x"),
            MakeRow(5, "ok", "2026-01-01T00:00:00Z", "MODIFY_SETTINGS", "x"),
        };
        var result = RestorePointService.ParseRestorePoints(rows);
        Assert.Single(result);
        Assert.Equal(5, result[0].SequenceNumber);
    }

    [Fact]
    public void Parse_HandlesUInt32SequenceFromWmi()
    {
        // WMI commonly surfaces SequenceNumber as a uint; the parser must coerce it.
        var rows = new[] { MakeRow((uint)42, "wmi", "2026-01-01T00:00:00Z", "MODIFY_SETTINGS", "x") };
        var result = RestorePointService.ParseRestorePoints(rows);
        Assert.Single(result);
        Assert.Equal(42, result[0].SequenceNumber);
    }

    [Fact]
    public void Parse_MissingOptionalFields_DefaultToEmpty()
    {
        var rows = new[] { MakeRow(1, null, null, null, null) };
        var result = RestorePointService.ParseRestorePoints(rows);
        Assert.Single(result);
        Assert.Equal("", result[0].Description);
        Assert.Equal("", result[0].RestorePointType);
        Assert.Equal(default, result[0].CreationTime);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
        => Assert.Empty(RestorePointService.ParseRestorePoints([]));

    /// <summary>
    /// A null collection is rejected at the boundary, naming the argument — not dereferenced.
    /// </summary>
    /// <remarks>
    /// The sibling of <c>DebloaterService.ParsePackages</c>, same shape and same omission: per-element nulls
    /// handled, the collection itself not, on a method that is public and documented for direct use (#2259).
    /// Fixed together because fixing only the one that happened to be found is how the second one gets found
    /// the same way later.
    /// </remarks>
    [Fact]
    public void Parse_NullInput_ThrowsNamingTheArgument()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => RestorePointService.ParseRestorePoints(null!));
        Assert.Equal("objects", ex.ParamName);
    }

    [Fact]
    public async Task CreateAsync_PowerShellHostUnavailable_ReturnsFalse()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<System.Collections.ObjectModel.Collection<PSObject>>>(
                _ => throw new RuntimeException(
                    "Windows PowerShell 5.1 is unavailable."));
        var service = new RestorePointService(runner);

        var created = await service.CreateAsync("Before changes");

        Assert.False(created);
    }

    private static IPowerShellRunner RunnerReturning(params object[] output)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new System.Collections.ObjectModel.Collection<PSObject>([.. output.Select(o => new PSObject(o))]));
        return runner;
    }

    [Fact]
    public async Task RestoreAsync_WhenTheScriptDoesNotConfirm_ReturnsFalse()
    {
        // #2436. The restore script ends in `catch { Write-Error $_; exit 1 }`. Write-Error is non-terminating and
        // RunAsync returns normally when only the error stream has records, so "did not throw" is not "started":
        // a failed Restore-Computer used to read as "Restore initiated — the system will restart."
        var service = new RestorePointService(RunnerReturning());

        Assert.False(await service.RestoreAsync(42));
    }

    [Fact]
    public async Task RestoreAsync_WhenTheScriptConfirms_ReturnsTrue()
    {
        var service = new RestorePointService(RunnerReturning(RestorePointService.RestoreStartedSentinel));

        Assert.True(await service.RestoreAsync(42));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAsync_IsTrueExactlyWhenTheScriptConfirms(bool confirmed)
    {
        // Other output — a warning line, a stray value — is not a confirmation. Only the sentinel, which the script
        // prints after Checkpoint-Computer returned without a warning or an error, is.
        var service = new RestorePointService(confirmed
            ? RunnerReturning("something else", RestorePointService.CreateOkSentinel)
            : RunnerReturning("something else"));

        Assert.Equal(confirmed, await service.CreateAsync("Before changes"));
    }

    [Fact]
    public void BuildCreateScript_StopsOnTheWarningWindowsUsesForTheDailyLimit()
    {
        // The shape that does the work: the rate-limit refusal is a WARNING in Windows PowerShell 5.1, so it is
        // -WarningAction Stop on the Checkpoint-Computer command itself that keeps the sentinel from printing.
        // The integration suite runs this text in real PowerShell; this pins it in the blocking one.
        var checkpoint = RestorePointService.BuildCreateScript("d")
            .Split(';')
            .Single(part => part.Contains("Checkpoint-Computer", StringComparison.Ordinal));

        Assert.Contains("-WarningAction Stop", checkpoint, StringComparison.Ordinal);
        Assert.Contains("-ErrorAction Stop", checkpoint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bob's point", "'Bob''s point'")]
    [InlineData("x'; Remove-Item C:\\Windows; '", "'x''; Remove-Item C:\\Windows; '''")]
    public void BuildCreateScript_KeepsTheDescriptionInsideOneQuotedString(string description, string expected)
    {
        // The description is the one user-supplied value in the script, and single-quote escaping is the only
        // thing standing between it and the command line — so it is asserted, not assumed.
        Assert.Contains($"-Description {expected} ", RestorePointService.BuildCreateScript(description), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildCreateScript_WithNoDescription_UsesTheDefaultName(string? description)
    {
        Assert.Contains("-Description 'SysManager Restore Point' ",
            RestorePointService.BuildCreateScript(description), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRestoreScript_ConfirmsOnlyAfterRestoreComputerReturned()
    {
        var script = RestorePointService.BuildRestoreScript(42);

        var restore = script.IndexOf("Restore-Computer -RestorePoint 42", StringComparison.Ordinal);
        var sentinel = script.IndexOf(RestorePointService.RestoreStartedSentinel, StringComparison.Ordinal);
        var catchBlock = script.IndexOf("catch", StringComparison.Ordinal);
        Assert.True(restore >= 0 && sentinel > restore && sentinel < catchBlock,
            "the confirmation must follow Restore-Computer inside the try, so a failure never prints it");
    }

    // ---------- TypeDisplay mapping ----------

    [Theory]
    [InlineData("APPLICATION_INSTALL", "App install")]
    [InlineData("APPLICATION_UNINSTALL", "App uninstall")]
    [InlineData("DEVICE_DRIVER_INSTALL", "Driver install")]
    [InlineData("MODIFY_SETTINGS", "Manual / settings")]
    [InlineData("CANCELLED_OPERATION", "Cancelled operation")]
    [InlineData("SOMETHING_NEW", "SOMETHING_NEW")]
    public void TypeDisplay_MapsKnownTypes(string raw, string expected)
    {
        var rp = new RestorePoint(1, "d", new DateTime(2026, 1, 1), raw, "e");
        Assert.Equal(expected, rp.TypeDisplay);
    }

    [Fact]
    public void CreatedDisplay_FormatsTimestamp()
    {
        var rp = new RestorePoint(1, "d", new DateTime(2026, 1, 2, 13, 14, 0), "MODIFY_SETTINGS", "e");
        Assert.Equal("2026-01-02 13:14", rp.CreatedDisplay);
    }
}
