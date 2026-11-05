// SysManager · WindowsUpdateServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="WindowsUpdateService"/>'s pure classification/formatting
/// helpers. The COM-lifetime fixes (releasing every IUpdate child RCW) can't be unit
/// tested without a live WUA feed, but these tests pin that the title-only
/// classification path — restructured when the Categories collection release was
/// wrapped in try/finally — still behaves identically.
/// </summary>
public class WindowsUpdateServiceTests
{
    [Theory]
    [InlineData("2024-06 Cumulative Update for Windows 11", "Cumulative")]
    [InlineData("Security Update for Microsoft Windows", "Security")]
    [InlineData("Windows Malicious Software Removal Tool - Defender", "Defender")]
    [InlineData("Security Intelligence Update for Microsoft Defender Antivirus", "Defender")]
    [InlineData("NVIDIA driver update", "Driver")]
    [InlineData("Intel - Firmware", "Driver")]
    [InlineData("Servicing Stack Update for Windows", "Servicing")]
    [InlineData("Feature update to Windows 11", "Update")]
    [InlineData("", "Update")]
    public void ClassifyCategory_TitleOnly_ClassifiesByKeyword(string title, string expected)
    {
        // u = null exercises the title-only path (no COM Categories collection).
        Assert.Equal(expected, WindowsUpdateService.ClassifyCategory(title, null));
    }

    [Fact]
    public void ClassifyCategory_DotNetSecurity_PrefersSecurity()
    {
        // "Security Update" is checked before ".NET", so a .NET security update is Security.
        Assert.Equal("Security", WindowsUpdateService.ClassifyCategory(".NET 8 Security Update", null));
    }

    [Fact]
    public void ClassifyCategory_DotNetNonSecurity_IsDotNet()
    {
        Assert.Equal(".NET", WindowsUpdateService.ClassifyCategory("Update for .NET Runtime", null));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(157286400, "150.0 MB")]
    [InlineData(2147483648, "2.0 GB")]
    public void FormatSize_FormatsHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, WindowsUpdateService.FormatSize(bytes));
    }

    // ── A failed download or install ─────────────────────────────────────────

    [Fact]
    public void AFailure_ShowsTheErrorWindowsReported()
    {
        // The row showed WUA's result code alone, 4 for nearly every failure, which no search can explain.
        Assert.Equal("Failed (install 0x800F0922)", WindowsUpdateService.FailureStatus("install", 4, unchecked((int)0x800F0922)));
        Assert.Equal("Failed (download 0x80240438)", WindowsUpdateService.FailureStatus("download", 4, unchecked((int)0x80240438)));
    }

    [Fact]
    public void AFailureWithNoError_FallsBackToTheResultCode()
        => Assert.Equal("Failed (install code 5)", WindowsUpdateService.FailureStatus("install", 5, 0));

    [Theory]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    public void TheUpdatesOwnCode_DecidesASucceededWithErrorsResult(int forTheUpdate, int expected)
        => Assert.Equal(expected, WindowsUpdateService.EffectiveResultCode(3, () => forTheUpdate));

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    public void AnyOtherOverallCode_StandsWithoutAskingTheUpdate(int overall)
        => Assert.Equal(overall, WindowsUpdateService.EffectiveResultCode(overall,
            () => throw new InvalidOperationException("the update's own result must not be read")));

    // ---------- the pill from what Windows Update says the update is, in any language (#2791) ----------

    private const int DriverUpdate = 2;

    /// <summary>Windows Update's classification of security updates, as this PC's own update store names it.</summary>
    private const string SecurityUpdates = "0FA1201D-4330-4FA8-8AE9-B877473B6441";

    /// <summary>The product category every update also carries, with an ID that names no classification.</summary>
    private static FakeCategory Product => new("A product", "00000000-0000-0000-0000-000000000001");

    [Theory]
    // German Windows: the driver classification is "Treiber", and a driver's title has no English word in it.
    [InlineData("Intel Corporation - Chipsatz - 10.1.19913.8607", "Treiber", WindowsUpdateService.DriversClassification,
        "Driver")]
    // French Windows: a feature upgrade's classification is not called "Upgrades" either.
    [InlineData("Windows 11, version 25H2", "Mises à niveau", WindowsUpdateService.UpgradesClassification,
        "Feature upgrade")]
    // Romanian Windows: a definition update whose title does not name Defender.
    [InlineData("Actualizare de definiții - 1.441.10.0", "Actualizări de definiții",
        WindowsUpdateService.DefinitionUpdatesClassification, "Defender")]
    public void TheClassificationsId_DecidesThePill_WhateverItIsCalled(
        string title, string categoryName, string categoryId, string expected)
    {
        var update = new FakeUpdate(Product, new FakeCategory(categoryName, categoryId));

        Assert.Equal(expected, WindowsUpdateService.ClassifyCategory(title, update));
    }

    [Fact]
    public void ADriverUpdate_IsADriver_EvenWithNoClassificationToGoBy()
    {
        var update = new FakeUpdate(Product) { Type = DriverUpdate };

        Assert.Equal("Driver", WindowsUpdateService.ClassifyCategory("Realtek - Extension - 10.0.26100.1", update));
    }

    [Fact]
    public void AnUpdateWithNothingToGoBy_FallsBackToItsTitle()
    {
        var update = new FakeUpdate(Product);

        Assert.Equal("Cumulative",
            WindowsUpdateService.ClassifyCategory("2026-10 Cumulative Update for Windows 11", update));
    }

    // ---------- the scan, over an agent of the test's own ----------

    private static WindowsUpdateService AgentOver(FakeUpdateSession session) =>
        new(() => session, () => new FakeCollection<FakeUpdate>());

    [Fact]
    public async Task TheScan_AsksForWhatIsPendingAndNotHidden_AndListsEachUpdateItFinds()
    {
        var session = new FakeUpdateSession();
        session.Pending.Add(new FakeUpdate(Product, new FakeCategory("Security Updates", SecurityUpdates))
        {
            Title = "2026-10 Cumulative Update for Windows 11 (KB5031234)",
            Id = "cumulative",
            KbNumbers = ["5031234"],
            MaxDownloadSize = 734003200m,
        });
        var drivers = new FakeCategory("Drivers", WindowsUpdateService.DriversClassification);
        session.Pending.Add(new FakeUpdate(Product, drivers)
        {
            Title = "Intel - System - 10.1.1.44",
            Id = "driver",
            Type = DriverUpdate,
            MaxDownloadSize = 2048m,
        });
        var progress = new SyncProgress<string>();

        var found = await AgentOver(session).ScanAsync(progress, CancellationToken.None);

        Assert.Equal([WindowsUpdateService.PendingCriteria], session.Searches);
        Assert.Equal(["Connecting to Windows Update…", "Found 2 update(s)."], progress.Reports);
        Assert.Equal(
            [("cumulative", "KB5031234", "700.0 MB", "Cumulative"), ("driver", "", "2.0 KB", "Driver")],
            found.Select(e => (e.UpdateId, e.KB, e.SizeDisplay, e.Category)));
    }

    [Fact]
    public async Task AScanWhoseSearchFails_ThrowsTheAgentsError()
    {
        var session = new FakeUpdateSession
        {
            SearchFails = new COMException("search failed", unchecked((int)0x8024402C)),
        };

        var error = await Assert.ThrowsAsync<COMException>(() => AgentOver(session).ScanAsync());

        Assert.Equal(unchecked((int)0x8024402C), error.HResult);
    }

    [Fact]
    public void TheAgent_WantsBothFactories()
    {
        Assert.Throws<ArgumentNullException>(() => new WindowsUpdateService(null!, () => new object()));
        Assert.Throws<ArgumentNullException>(() => new WindowsUpdateService(() => new object(), null!));
    }
}
