// SysManager · RestorePointServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management.Automation;
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
