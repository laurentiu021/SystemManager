// SysManager · UninstallerRegistryDetailsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// What the Uninstaller takes from Windows' uninstall lists for each app winget lists: the commands that remove it and
/// its folder from the one entry that is its own, and its size and publisher from the first entry of its name in the
/// order Windows keeps the lists.
/// </summary>
/// <remarks>
/// The read went straight to the machine's hives, so its two tests checked only that it did not throw, and every parse
/// test read the uninstall list of the machine running it (#2741). The lists here are throwaway HKCU subkeys standing
/// in for HKLM and HKCU, and no command is ever run.
/// </remarks>
public sealed class UninstallerRegistryDetailsTests : IDisposable
{
    private const string SixtyFourBit = UninstallerService.UninstallKey;
    private const string ThirtyTwoBit = UninstallerService.UninstallKeyWow64;

    private readonly string _rootName = @"Software\SysManagerTests\UninstallDetails_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _machine;
    private readonly RegistryKey _user;

    public UninstallerRegistryDetailsTests()
    {
        _machine = Registry.CurrentUser.CreateSubKey(_rootName + @"\HKLM", writable: true);
        _user = Registry.CurrentUser.CreateSubKey(_rootName + @"\HKCU", writable: true);
    }

    public void Dispose()
    {
        _machine.Dispose();
        _user.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);
    }

    /// <summary>An uninstall entry in <paramref name="list"/> under <paramref name="root"/>, with the values given.</summary>
    private static void Entry(RegistryKey root, string list, string displayName, params (string Name, object Value)[] values)
    {
        using var key = root.CreateSubKey($@"{list}\{Guid.NewGuid():N}", writable: true);
        key.SetValue("DisplayName", displayName);
        foreach (var (name, value) in values) key.SetValue(name, value);
    }

    /// <summary>An uninstall entry under the key <paramref name="key"/>, the name an <c>ARP\</c> id gives it.</summary>
    private static void EntryAt(RegistryKey root, string list, string key, string displayName,
                                params (string Name, object Value)[] values)
    {
        using var entry = root.CreateSubKey($@"{list}\{key}", writable: true);
        entry.SetValue("DisplayName", displayName);
        foreach (var (name, value) in values) entry.SetValue(name, value);
    }

    /// <summary>An app as winget lists it, enriched from the throwaway lists.</summary>
    private InstalledApp Enriched(string name = "Test App")
    {
        var app = new InstalledApp { Name = name, Id = "Test.App" };
        Enrich(app);
        return app;
    }

    /// <summary>Enriches <paramref name="apps"/>, all in one read, as the list is enriched.</summary>
    private void Enrich(params InstalledApp[] apps) =>
        new UninstallerService(Substitute.For<IPowerShellRunner>(), () => false, machineRoot: _machine, userRoot: _user)
            .EnrichFromRegistry([.. apps]);

    [Fact]
    public void AnAppWithAnEntry_GetsItsSizePublisherFolderAndCommands()
    {
        Entry(_machine, SixtyFourBit, "Test App",
            ("EstimatedSize", 2048),
            ("Publisher", "Test Publisher"),
            ("InstallLocation", @"  C:\Program Files\Test App  "),
            ("UninstallString", @"""C:\Program Files\Test App\uninst.exe"""),
            ("QuietUninstallString", @"""C:\Program Files\Test App\uninst.exe"" /S"));

        var app = Enriched();

        Assert.Equal(2048 * 1024L, app.SizeBytes);   // the entry gives it in KB
        Assert.Equal("Test Publisher", app.Publisher);
        Assert.Equal(@"C:\Program Files\Test App", app.InstallLocation);
        Assert.Equal(@"""C:\Program Files\Test App\uninst.exe""", app.UninstallString);
        Assert.Equal(@"""C:\Program Files\Test App\uninst.exe"" /S", app.QuietUninstallString);
    }

    [Fact]
    public void TheListsAreReadInWindowsOrder_SixtyFourBitThenThirtyTwoBitThenTheUsers()
    {
        Entry(_user, SixtyFourBit, "Test App", ("Publisher", "the user's"), ("UninstallString", "app.exe"));
        Entry(_machine, ThirtyTwoBit, "Test App", ("Publisher", "32-bit"), ("UninstallString", "app.exe"));
        Entry(_machine, SixtyFourBit, "Test App", ("Publisher", "64-bit"), ("UninstallString", "app.exe"));

        var app = Enriched();

        Assert.Equal(("64-bit", "app.exe"), (app.Publisher, app.UninstallString));
    }

    // ── Two programs of one name ──────────────────────────────────────────
    // A 32-bit and a 64-bit build, a copy for one user and one for all, two versions side by side. Matched by name, the
    // first row of the name took the first entry's commands, so it ran the other copy's uninstaller, and a second row
    // of the name had none.

    [Fact]
    public void ARowWithAnArpId_TakesTheEntryItNames_AndAnotherRowOfTheNameTakesItsOwn()
    {
        EntryAt(_machine, SixtyFourBit, "{A}", "Test App",
            ("UninstallString", "x64.exe"), ("InstallLocation", @"C:\Program Files\Test App"));
        EntryAt(_machine, ThirtyTwoBit, "{B}", "Test App",
            ("UninstallString", "x86.exe"), ("InstallLocation", @"C:\Program Files (x86)\Test App"));
        var x86 = new InstalledApp { Name = "Test App", Id = @"ARP\Machine\X86\{B}" };
        var x64 = new InstalledApp { Name = "Test App", Id = @"ARP\Machine\X64\{A}" };

        Enrich(x86, x64);

        Assert.Equal(("x86.exe", @"C:\Program Files (x86)\Test App"), (x86.UninstallString, x86.InstallLocation));
        Assert.Equal(("x64.exe", @"C:\Program Files\Test App"), (x64.UninstallString, x64.InstallLocation));
    }

    [Fact]
    public void AUsersArpId_IsLookedUpInTheUsersList()
    {
        EntryAt(_machine, SixtyFourBit, "Test App", "Test App", ("UninstallString", "machine.exe"));
        EntryAt(_user, SixtyFourBit, "Test App", "Test App", ("UninstallString", "user.exe"));
        var app = new InstalledApp { Name = "Test App", Id = @"ARP\User\X64\Test App" };

        Enrich(app);

        Assert.Equal("user.exe", app.UninstallString);
    }

    [Fact]
    public void AnArpIdNamingNoEntry_TakesNothingFromAnotherEntryOfItsName()
    {
        EntryAt(_machine, SixtyFourBit, "{A}", "Test App", ("UninstallString", "other.exe"));
        var app = new InstalledApp { Name = "Test App", Id = @"ARP\Machine\X64\{Gone}" };

        Enrich(app);

        Assert.Equal(("", ""), (app.UninstallString, app.QuietUninstallString));
    }

    [Fact]
    public void TwoProgramsOfOneName_GiveARowWithoutAnArpId_NeitherCommandNorFolder()
    {
        // Nothing says which is the row's own. winget removes a row it lists by the row's own id instead.
        EntryAt(_machine, SixtyFourBit, "{A}", "Test App", ("Publisher", "First"),
            ("UninstallString", "x64.exe"), ("InstallLocation", @"C:\Program Files\Test App"));
        EntryAt(_machine, ThirtyTwoBit, "{B}", "Test App", ("Publisher", "Second"),
            ("UninstallString", "x86.exe"), ("InstallLocation", @"C:\Program Files (x86)\Test App"));

        var app = Enriched();

        Assert.Equal(("First", "", "", ""),
            (app.Publisher, app.UninstallString, app.QuietUninstallString, app.InstallLocation));
    }

    [Fact]
    public void AmongEntriesOfOneName_TheRowsVersionPicksItsOwn()
    {
        EntryAt(_machine, SixtyFourBit, "{A}", "Test App", ("DisplayVersion", "1.0"), ("UninstallString", "old.exe"));
        EntryAt(_machine, ThirtyTwoBit, "{B}", "Test App", ("DisplayVersion", "2.0"), ("UninstallString", "new.exe"));
        var app = new InstalledApp { Name = "Test App", Id = "Test.App", Version = "2.0" };

        Enrich(app);

        Assert.Equal("new.exe", app.UninstallString);
    }

    [Fact]
    public void EntriesOfOneName_ThatRunTheSameCommandFromTheSameFolder_GiveIt()
    {
        EntryAt(_machine, SixtyFourBit, "{A}", "Test App", ("UninstallString", "same.exe"));
        EntryAt(_user, SixtyFourBit, "{A}", "Test App", ("UninstallString", "same.exe"));

        Assert.Equal("same.exe", Enriched().UninstallString);
    }

    [Fact]
    public void AnAppOnlyTheUsersListHas_IsFoundThere()
    {
        Entry(_machine, SixtyFourBit, "Another App", ("Publisher", "another"), ("UninstallString", "another.exe"));
        Entry(_user, SixtyFourBit, "Test App", ("Publisher", "the user's"), ("UninstallString", "user.exe"));

        var app = Enriched();

        Assert.Equal(("the user's", "user.exe"), (app.Publisher, app.UninstallString));
    }

    [Fact]
    public void BothCommandsComeFromTheEntryWithAnUninstallCommand()
    {
        // A leftover with only a quiet command ahead of the app's own entry: its command is the one launched, so it
        // must not be kept beside the other entry's.
        Entry(_machine, SixtyFourBit, "Test App", ("QuietUninstallString", "leftover.exe /S"));
        Entry(_machine, ThirtyTwoBit, "Test App", ("UninstallString", "current.exe"));

        var app = Enriched();

        Assert.Equal(("current.exe", ""), (app.UninstallString, app.QuietUninstallString));
    }

    [Fact]
    public void AnAppWhoseOnlyEntryHasAQuietCommand_KeepsIt()
    {
        Entry(_machine, SixtyFourBit, "Test App", ("QuietUninstallString", "quiet.exe /S"));

        var app = Enriched();

        Assert.Equal(("", "quiet.exe /S"), (app.UninstallString, app.QuietUninstallString));
    }

    [Fact]
    public void ANameInAnotherCase_IsTheSameApp()
    {
        Entry(_machine, SixtyFourBit, "TEST APP", ("UninstallString", "test.exe"));

        Assert.Equal("test.exe", Enriched("Test App").UninstallString);
    }

    [Fact]
    public void AnEntryNamedDifferently_GivesNothing()
    {
        Entry(_machine, SixtyFourBit, "Test App Helper", ("Publisher", "helper"), ("UninstallString", "helper.exe"));

        var app = Enriched();

        Assert.Equal(("", ""), (app.Publisher, app.UninstallString));
    }

    [Fact]
    public async Task TheInstalledList_IsEnrichedFromTheServicesOwnLists()
    {
        Entry(_machine, SixtyFourBit, "Git", ("Publisher", "The Git Development Community"), ("UninstallString", "git.exe"));
        var winget = WingetQueryFailureTests.WingetThatPrints(0,
            "Name                         Id                           Version    Source",
            "-------------------------------------------------------------------------",
            "Git                          Git.Git                      2.45.1     winget",
            "1 packages installed.");
        var service = new UninstallerService(winget, () => false, machineRoot: _machine, userRoot: _user);

        var git = Assert.Single(await service.ListInstalledAsync());

        Assert.Equal(("Git.Git", "The Git Development Community", "git.exe"), (git.Id, git.Publisher, git.UninstallString));
    }

    [Fact]
    public void ListsThatAreNotThere_GiveNothing_AndDoNotThrow()
    {
        var app = Enriched();

        Assert.Equal((0L, "", ""), (app.SizeBytes, app.Publisher, app.UninstallString));
    }
}
