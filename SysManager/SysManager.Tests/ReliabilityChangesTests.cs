// SysManager · ReliabilityChangesTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// How Windows' reliability records become rows on Recent Changes (#1507).
/// </summary>
/// <remarks>
/// Each record is written the way Windows writes it, message text included, because names and statuses are read out
/// of that text. The products are made up; the shapes are copied from a real history.
/// </remarks>
public class ReliabilityChangesTests
{
    private const string WindowsUpdate = "Microsoft-Windows-WindowsUpdateClient";
    private static readonly DateTime Evening = new(2026, 10, 5, 21, 31, 25);

    private static ReliabilityRecord UpdateInstalled(string title, DateTime? when = null, string? product = null) => new(
        when ?? Evening, WindowsUpdate, 19, product ?? title,
        $"Installation Successful: Windows successfully installed the following update: {title}");

    private static ReliabilityRecord UpdateFailed(string title, DateTime? when = null, string? product = null) => new(
        when ?? Evening, WindowsUpdate, 20, product ?? title,
        $"Installation Failure: Windows failed to install the following update with error 0x80073D02: {title}.");

    private static ReliabilityRecord MsiInstalled(string product, int status = 0, DateTime? when = null) => new(
        when ?? Evening, "MsiInstaller", 1033, product,
        $"Windows Installer installed the product. Product Name: {product}. Product Version: 4.2.1. Product Language: 1033. "
        + $"Manufacturer: Contoso. Installation success or error status: {status}.");

    private static ReliabilityRecord MsiRemoved(string product, int status = 0, DateTime? when = null) => new(
        when ?? Evening, "MsiInstaller", 1034, product,
        $"Windows Installer removed the product. Product Name: {product}. Product Version: 4.2.1. Product Language: 1033. "
        + $"Manufacturer: Contoso. Removal success or error status: {status}.");

    private static IReadOnlyList<ChangeEvent> Changes(params ReliabilityRecord[] records) => ReliabilityChanges.Parse(records).Changes;

    // ── Windows Update ─────────────────────────────────────────────────────

    [Fact]
    public void AStoreAppUpdate_IsNamedWithoutTheStoreIdInFrontOfIt()
    {
        var change = Assert.Single(Changes(UpdateInstalled("9NRZT3Q9R3DL-Contoso.PhotoEditor")));

        Assert.Equal(ChangeKind.StoreAppUpdate, change.Kind);
        Assert.Equal("Contoso.PhotoEditor", change.Subject);
        Assert.Equal("Microsoft Store", change.Who);
        Assert.Equal(Evening, change.When);
        Assert.Null(change.Since);
    }

    [Fact]
    public void TheSameRecordWrittenFourTimes_IsOneChange()
    {
        // Windows wrote one Store update four times over, to the second, in the history this was copied from.
        var record = UpdateInstalled("9NRZT3Q9R3DL-Contoso.PhotoEditor");

        Assert.Single(Changes(record, record, record, record));
    }

    [Theory]
    [InlineData("Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.419.85.0) - Current Channel (Broad)")]
    [InlineData("Definition Update for Microsoft Defender Antivirus - KB2267602 (Version 1.419.85.0)")]
    public void ADefenderDefinitionsUpdate_IsItsOwnKind(string title)
    {
        var change = Assert.Single(Changes(UpdateInstalled(title)));

        Assert.Equal(ChangeKind.DefenderUpdate, change.Kind);
        Assert.Equal("Windows Update", change.Who);
    }

    [Fact]
    public void ADriverUpdate_NamesTheVendorAndTheDevice_AndSaysTheVersionUnder()
    {
        var change = Assert.Single(Changes(UpdateInstalled("Contoso Ltd. - Display - 31.0.101.4502")));

        Assert.Equal(ChangeKind.DriverUpdate, change.Kind);
        Assert.Equal("Contoso Ltd. Display", change.Subject);
        Assert.Equal("Version 31.0.101.4502", change.Detail);
        Assert.Equal("Windows Update", change.Who);
    }

    [Theory]
    [InlineData("2026-09 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5065426)")]
    [InlineData("Update for Windows Security platform - KB5007651 (Version 10.0.29429.1000)")]
    public void AnyOtherUpdate_IsAWindowsUpdate_UnderItsOwnTitle(string title)
    {
        // The second has a " - " in it as a driver does, and no version after a second one, so it is not a driver.
        var change = Assert.Single(Changes(UpdateInstalled(title)));

        Assert.Equal(ChangeKind.WindowsUpdate, change.Kind);
        Assert.Equal(title, change.Subject);
        Assert.Equal("Windows Update", change.Who);
        Assert.Equal("", change.Detail);
    }

    [Fact]
    public void AFailedStoreUpdate_SaysWindowsTriesAgain()
    {
        var change = Assert.Single(Changes(UpdateFailed("9N0DX20HK701-Contoso.Terminal")));

        Assert.Equal(ChangeKind.UpdateFailed, change.Kind);
        Assert.Equal("Contoso.Terminal", change.Subject);
        Assert.Equal("Microsoft Store", change.Who);
        Assert.Equal("Windows will try again on its own", change.Detail);
    }

    [Fact]
    public void AFailedWindowsUpdate_IsFromWindowsUpdate()
    {
        var change = Assert.Single(Changes(UpdateFailed("2026-09 Cumulative Update for Windows 11 (KB5065426)")));

        Assert.Equal(ChangeKind.UpdateFailed, change.Kind);
        Assert.Equal("Windows Update", change.Who);
    }

    [Fact]
    public void WithNoProductName_TheUpdateIsNamedFromTheEndOfTheMessage()
    {
        var change = Assert.Single(Changes(UpdateFailed("9N0DX20HK701-Contoso.Terminal", product: "")));

        Assert.Equal("Contoso.Terminal", change.Subject);   // the full stop that ends the message is not part of it
    }

    // ── Windows Installer ──────────────────────────────────────────────────

    [Fact]
    public void AProgramWindowsInstallerInstalled_IsListedFromItsRecord()
    {
        var change = Assert.Single(Changes(MsiInstalled("Contoso Photo Editor")));

        Assert.Equal(ChangeKind.ProgramInstalled, change.Kind);
        Assert.Equal("Contoso Photo Editor", change.Subject);
        Assert.Equal("An installer", change.Who);
        Assert.Equal("Recorded by Windows Installer", change.Detail);
    }

    [Fact]
    public void AProgramWindowsInstallerRemoved_IsListedFromItsRecord()
    {
        var change = Assert.Single(Changes(MsiRemoved("Contoso Photo Editor")));

        Assert.Equal(ChangeKind.ProgramRemoved, change.Kind);
        Assert.Equal("An uninstaller", change.Who);
    }

    [Theory]
    [InlineData(1603)]
    [InlineData(-1)]
    public void AnInstallOrRemovalThatFailed_IsNotAChange(int status)
    {
        Assert.Empty(Changes(MsiInstalled("Contoso Photo Editor", status), MsiRemoved("Contoso Photo Editor", status)));
    }

    [Fact]
    public void TheTwoRecordsWindowsInstallerWritesForOneInstall_AreOneChange()
    {
        // 1033 names the product in its own field; 11707 often only in its message, after "Product:".
        var completed = new ReliabilityRecord(Evening.AddSeconds(3), "MsiInstaller", 11707, "",
            "Product: Contoso Photo Editor -- Installation completed successfully.");

        var change = Assert.Single(Changes(MsiInstalled("Contoso Photo Editor"), completed));

        Assert.Equal("Contoso Photo Editor", change.Subject);
    }

    [Fact]
    public void TheTwoRecordsWindowsInstallerWritesForOneRemoval_AreOneChange()
    {
        var completed = new ReliabilityRecord(Evening.AddSeconds(3), "MsiInstaller", 11724, "",
            "Product: Contoso Photo Editor -- Removal completed successfully.");

        Assert.Equal(ChangeKind.ProgramRemoved, Assert.Single(Changes(MsiRemoved("Contoso Photo Editor"), completed)).Kind);
    }

    [Fact]
    public void TheSameProgramInstalledOnTwoDays_IsTwoChanges()
    {
        // A runtime that reinstalls itself every morning really did so each day, in the history this was copied from.
        var changes = Changes(MsiInstalled("Contoso Runtime"), MsiInstalled("Contoso Runtime", when: Evening.AddDays(1)));

        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void AWindowsInstallerRecordThatNamesNoProduct_IsLeftOut()
    {
        var nameless = new ReliabilityRecord(Evening, "MsiInstaller", 11707, "", "Installation completed successfully.");

        Assert.Empty(Changes(nameless));
    }

    // ── Problems, and everything else ──────────────────────────────────────

    [Fact]
    public void CrashesAndHangs_AreCountedAsProblems_NotListedAsChanges()
    {
        var crash = new ReliabilityRecord(Evening, "Application Error", 1000, "contoso.exe",
            "Faulting application name: contoso.exe, version: 4.2.1.0");
        var hang = new ReliabilityRecord(Evening.AddMinutes(5), "Application Hang", 1002, "contoso.exe",
            "The program contoso.exe version 4.2.1.0 stopped interacting with Windows and was closed.");

        var (changes, problems) = ReliabilityChanges.Parse([crash, hang]);

        Assert.Empty(changes);
        Assert.Equal([new ProblemEvent(Evening, StoppedResponding: false), new ProblemEvent(Evening.AddMinutes(5), StoppedResponding: true)],
            problems);
    }

    [Fact]
    public void TheSameCrashWrittenTwice_IsCountedOnce()
    {
        var crash = new ReliabilityRecord(Evening, "Application Error", 1000, "contoso.exe",
            "Faulting application name: contoso.exe, version: 4.2.1.0");

        Assert.Single(ReliabilityChanges.Parse([crash, crash]).Problems);
    }

    [Fact]
    public void AKindOfRecordThatIsNotAChange_IsLeftOut()
    {
        var restart = new ReliabilityRecord(Evening, "Microsoft-Windows-Kernel-General", 12, "", "The operating system started.");

        var (changes, problems) = ReliabilityChanges.Parse([restart]);

        Assert.Empty(changes);
        Assert.Empty(problems);
    }

    [Fact]
    public void TheChanges_AreNewestFirst()
    {
        var changes = Changes(
            MsiInstalled("Older", when: Evening.AddHours(-2)),
            UpdateInstalled("9NRZT3Q9R3DL-Contoso.Newest", when: Evening.AddHours(1)),
            MsiRemoved("Middle"));

        Assert.Equal(["Contoso.Newest", "Middle", "Older"], changes.Select(c => c.Subject));
    }

    [Fact]
    public void Parse_RefusesNoRecords()
    {
        Assert.Throws<ArgumentNullException>(() => ReliabilityChanges.Parse(null!));
    }
}
