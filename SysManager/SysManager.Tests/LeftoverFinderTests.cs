// SysManager · LeftoverFinderTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// What the Uninstaller offers as left behind, how sure it says it is, and, above all, what it refuses to offer
/// (#1527). Runs against a temp tree standing in for the profile, Program Files, ProgramData and Windows; the
/// registry is a set of key paths and a list of uninstall entries, so nothing of the PC running the suite is read.
/// </summary>
public sealed class LeftoverFinderTests : IDisposable
{
    private readonly TempLeftoverEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private static UninstallProbe Probe(string name = "Acme Notes", string publisher = "Acme Software", string id = "",
                                        string installLocation = "") =>
        new(name, publisher, id, installLocation);

    private LeftoverFindings Find(UninstallProbe probe, params UninstallProbe[] stillInstalled) =>
        LeftoverFinder.Find(probe, _env, stillInstalled);

    private string Roaming(string name) => Path.Combine(_env.RoamingAppData, name);
    private string Local(string name) => Path.Combine(_env.LocalAppData, name);
    private string ProgramData(string name) => Path.Combine(_env.ProgramData, name);
    private string ProgramFiles(string name) => Path.Combine(_env.ProgramFiles, name);

    // ── What is offered, and how sure ─────────────────────────────────────

    [Fact]
    public void TheInstallFolder_IsCertain_AndTheOnlyThingTickedFromTheStart()
    {
        _env.IsElevated = true;
        var install = TempLeftoverEnvironment.Folder(ProgramFiles("Acme Notes"));
        TempLeftoverEnvironment.Folder(Roaming("Acme Notes"));

        var findings = Find(Probe(installLocation: install));

        var certain = Assert.Single(findings.Items, i => i.Confidence == LeftoverConfidence.Certain);
        Assert.Equal(install, certain.Location);
        Assert.Equal(LeftoverKind.Folder, certain.Kind);
        Assert.True(certain.IsSelected);
        Assert.Equal("The folder it was installed in", certain.Reason);

        var probably = Assert.Single(findings.Items, i => i.Confidence == LeftoverConfidence.Probably);
        Assert.Equal(Roaming("Acme Notes"), probably.Location);
        Assert.False(probably.IsSelected);
    }

    [Fact]
    public void FoldersNamedAfterTheApp_AreFoundByEveryNameItAnswersTo()
    {
        // The display name, the winget id's tail and the install folder's own name are three spellings of one app.
        _env.IsElevated = true;
        var install = TempLeftoverEnvironment.Folder(ProgramFiles(@"VideoLAN\VLC"));
        TempLeftoverEnvironment.Folder(Roaming("VLC media player"));
        TempLeftoverEnvironment.Folder(Local("VLC"));

        var findings = Find(Probe("VLC media player", "VideoLAN", "VideoLAN.VLC", install));

        Assert.Equal(
            [install, Roaming("VLC media player"), Local("VLC")],
            findings.Items.Where(i => i.Kind == LeftoverKind.Folder).Select(i => i.Location));
        Assert.Equal(["VLC media player", "VLC"], LeftoverFinder.AppNames(Probe("VLC media player", "", "VideoLAN.VLC", install)));
    }

    [Fact]
    public void ANameEndingInADot_IsShownAsTheFolderWindowsWouldOpen()
    {
        // Windows drops a trailing dot from a path, so "Acme Notes." opens "Acme Notes"; the row has to name that
        // folder, or the one shown is not the one removed.
        var folder = TempLeftoverEnvironment.Folder(Roaming("Acme Notes"));

        var item = Assert.Single(Find(Probe("Acme Notes.")).Items);

        Assert.Equal(folder, item.Location);
    }

    [Fact]
    public void TheAppsOwnKey_IsOffered_UntickedAndLabelled()
    {
        _env.RegistryKeys.Add(@"Software\Acme\Notes");

        var findings = Find(Probe("Acme Notes", "Acme", "Acme.Notes"));

        var key = Assert.Single(findings.Items);
        Assert.Equal(LeftoverKind.RegistryKey, key.Kind);
        Assert.Equal(LeftoverConfidence.OwnKey, key.Confidence);
        Assert.Equal(@"Software\Acme\Notes", key.Location);
        Assert.Equal(@"HKEY_CURRENT_USER\Software\Acme\Notes", key.DisplayLocation);
        Assert.False(key.IsSelected);
    }

    [Fact]
    public void AKeyThatDoesNotExist_IsNotOffered()
        => Assert.Empty(Find(Probe("Acme Notes", "Acme", "Acme.Notes")).Items);

    [Fact]
    public void APublisherFolder_IsAGuess_NamedAfterThePublisher()
    {
        TempLeftoverEnvironment.Folder(Local("Acme Software"));

        var findings = Find(Probe());

        var guess = Assert.Single(findings.Items);
        Assert.Equal(LeftoverConfidence.Guess, guess.Confidence);
        Assert.Equal(Local("Acme Software"), guess.Location);
        Assert.False(guess.IsSelected);
        Assert.Equal("Named after the publisher, and no other Acme Software app is installed", guess.Reason);
    }

    [Fact]
    public void APublisherFolderUnderProgramData_IsNeverOffered()
    {
        // ProgramData\<Vendor> is where a vendor's drivers and services keep their state, and those are mostly
        // hidden from the list; a guess there is a guess about something else's data.
        _env.IsElevated = true;
        TempLeftoverEnvironment.Folder(ProgramData("Acme Software"));

        var findings = Find(Probe());

        Assert.Empty(findings.Items);
        Assert.Empty(findings.NotOffered);
    }

    [Fact]
    public void AFolderUnderProgramDataOrProgramFiles_WithoutAdministratorRights_CannotBeTicked()
    {
        _env.IsElevated = false;
        var install = TempLeftoverEnvironment.Folder(ProgramFiles("Acme Notes"));
        TempLeftoverEnvironment.Folder(ProgramData("Acme Notes"));
        TempLeftoverEnvironment.Folder(Roaming("Acme Notes"));

        var items = Find(Probe(installLocation: install)).Items;

        Assert.All(items.Where(i => i.Location != Roaming("Acme Notes")), i =>
        {
            Assert.True(i.NeedsAdministrator, i.Location);
            Assert.False(i.CanSelect, i.Location);
            Assert.False(i.IsSelected, i.Location);
        });
        Assert.True(Assert.Single(items, i => i.Location == Roaming("Acme Notes")).CanSelect);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void AFolderFoundTwice_IsListedOnce_AtItsSurestConfidence()
    {
        // The install folder IS the folder named after the app: one row, Certain, not a second "Probably" beside it.
        var install = TempLeftoverEnvironment.Folder(Local("Acme Notes"));

        var findings = Find(Probe(installLocation: install));

        var only = Assert.Single(findings.Items);
        Assert.Equal(LeftoverConfidence.Certain, only.Confidence);
    }

    [Fact]
    public void AFolderInTheUsersOwnFolders_IsOnlyMentioned()
    {
        TempLeftoverEnvironment.Folder(Path.Combine(_env.Documents, "Acme Notes"));

        var findings = Find(Probe());

        Assert.Empty(findings.Items);
        Assert.Equal(
            $"Not offered: {Path.Combine(_env.Documents, "Acme Notes")} is in Documents, which SysManager never offers.",
            Assert.Single(findings.NotOffered));
    }

    // ── What is refused ───────────────────────────────────────────────────

    [Fact]
    public void AnInstallLocationThatIsAUsersFolder_IsRefused()
    {
        var findings = Find(Probe(installLocation: _env.Documents));

        Assert.Empty(findings.Items);
        Assert.Contains(findings.NotOffered, n => n.Contains("is in Documents, which SysManager never offers", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Windows", "is inside Windows, which SysManager never offers")]
    [InlineData("AppData", "holds folders SysManager never offers")]
    [InlineData("ProgramFiles", "holds folders SysManager never offers")]
    [InlineData("Own", "belongs to SysManager")]
    public void AnInstallLocationSysManagerNeverTouches_IsRefused_WithTheReason(string where, string reason)
    {
        _env.IsElevated = true;
        var path = where switch
        {
            "Windows" => TempLeftoverEnvironment.Folder(Path.Combine(_env.WindowsDirectory, "System32")),
            "AppData" => _env.RoamingAppData,
            "ProgramFiles" => _env.ProgramFiles,
            _ => TempLeftoverEnvironment.Folder(_env.OwnFolders[0]),
        };

        var findings = Find(Probe(installLocation: path));

        Assert.DoesNotContain(findings.Items, i => i.Location == path);
        Assert.Contains($"Not offered: {path} {reason}.", findings.NotOffered);
    }

    [Fact]
    public void AFolderThatHoldsTheProfile_IsRefused()
    {
        // An entry naming C:\Users as its install folder would otherwise take every profile with it.
        var users = Path.GetDirectoryName(_env.UserProfile)!;

        Assert.Equal("holds folders SysManager never offers", LeftoverFinder.Refusal(users, _env));
    }

    [Fact]
    public void TheRootOfADrive_IsRefused()
        => Assert.Equal("is the root of a drive", LeftoverFinder.Refusal(Path.GetPathRoot(_env.Root)!, _env));

    [Fact]
    public void AFolderWindowsAndManyAppsShare_IsRefused_EvenAsTheInstallLocation()
    {
        _env.IsElevated = true;
        var common = TempLeftoverEnvironment.Folder(ProgramFiles("Common Files"));

        var findings = Find(Probe(installLocation: common));

        Assert.Empty(findings.Items);
        Assert.Contains($"Not offered: {common} is a folder Windows and many apps share.", findings.NotOffered);
    }

    [Fact]
    public void AnInstallLocationOnANetworkShare_IsNeverLookedAt()
    {
        // Not even Directory.Exists: a share can take a minute to answer, and has no Recycle Bin to put things back from.
        var findings = Find(Probe(installLocation: @"\\fileserver\apps\Acme Notes"));

        Assert.Empty(findings.Items);
        Assert.Empty(findings.NotOffered);
        Assert.Equal("is not on one of this PC's own drives", LeftoverFinder.Refusal(@"\\fileserver\apps\Acme Notes", _env));
        Assert.Equal("is not on one of this PC's own drives", LeftoverFinder.Refusal(@"\\?\C:\Acme Notes", _env));
    }

    [Fact]
    public void AFolderOnARemovableDrive_IsRefused()
    {
        var install = TempLeftoverEnvironment.Folder(ProgramFiles("Acme Notes"));
        _env.DriveTypes[Path.GetPathRoot(_env.Root)!] = DriveType.Removable;

        var findings = Find(Probe(installLocation: install));

        Assert.Empty(findings.Items);
        Assert.Equal("is not on one of this PC's own drives", LeftoverFinder.Refusal(install, _env));
    }

    [Fact]
    public void AFolderThatIsALink_IsRefused()
    {
        var elsewhere = TempLeftoverEnvironment.Folder(Path.Combine(_env.Root, "Elsewhere"), bytes: 10);
        Symlinks.RequireJunction(Roaming("Acme Notes"), elsewhere);   // Dispose tears the tree down either way

        var findings = Find(Probe());

        Assert.Empty(findings.Items);
        Assert.Contains($"Not offered: {Roaming("Acme Notes")} is a link to another folder.", findings.NotOffered);
    }

    [Fact]
    public void AFolderReachedThroughALink_IsRefused()
    {
        var real = TempLeftoverEnvironment.Folder(Path.Combine(_env.Root, @"Elsewhere\Acme Notes"));
        var link = Path.Combine(_env.ProgramFiles, "Linked");
        Symlinks.RequireJunction(link, Path.GetDirectoryName(real)!);

        Assert.Equal("is reached through a link", LeftoverFinder.Refusal(Path.Combine(link, "Acme Notes"), _env));
    }

    [Fact]
    public void ANameAnotherInstalledAppAnswersTo_IsRefused()
    {
        TempLeftoverEnvironment.Folder(Roaming("Zoom"));

        var findings = Find(Probe("Zoom", "Zoom Video Communications"), new UninstallProbe("Zoom Outlook Plugin", "", "", ""));

        Assert.Empty(findings.Items);
        Assert.Contains($"Not offered: {Roaming("Zoom")} may belong to another installed app.", findings.NotOffered);
    }

    [Fact]
    public void ANameIsSharedOnlyAsWholeWords()
    {
        // "Steamworks Common Redistributables" is not Steam, and "Notes" alone is not "Acme Notes".
        Assert.False(LeftoverFinder.IsShared("Steam", [new UninstallProbe("Steamworks Common Redistributables", "", "", "")]));
        Assert.True(LeftoverFinder.IsShared("Steam", [new UninstallProbe("Steam Link", "", "", "")]));
        Assert.True(LeftoverFinder.IsShared("Acme", [new UninstallProbe("", "", "Acme.Other", "")]));
    }

    [Fact]
    public void APublisherAHiddenDriverPackageStillUses_IsRefused()
    {
        // The list on screen hides system components; the uninstall entries Windows holds do not.
        TempLeftoverEnvironment.Folder(Local("Acme Software"));
        _env.Registered.Add(new UninstallProbe("Acme Audio Driver", "Acme Software", "", ""));

        var findings = Find(Probe());

        Assert.Empty(findings.Items);
        Assert.Contains($"Not offered: {Local("Acme Software")} is shared with another installed Acme Software app.",
            findings.NotOffered);
    }

    [Fact]
    public void AFolderAnotherInstalledAppLivesIn_IsRefused_ThroughItsInstallFolderOrItsUninstaller()
    {
        // A vendor folder named as the install location while the vendor's other product still sits inside it.
        _env.IsElevated = true;
        var vendor = TempLeftoverEnvironment.Folder(ProgramFiles("Acme"));
        var other = TempLeftoverEnvironment.Folder(Path.Combine(vendor, "Other Product"));

        var byFolder = Find(Probe(installLocation: vendor), new UninstallProbe("Unrelated", "", "", other));
        Assert.Empty(byFolder.Items);
        Assert.Contains($"Not offered: {vendor} is also where another installed app lives.", byFolder.NotOffered);

        var uninstaller = $"\"{Path.Combine(other, "uninst.exe")}\" /S";
        _env.Registered.Add(new UninstallProbe("Unrelated", "", "", "", uninstaller));
        var byUninstaller = Find(Probe(installLocation: vendor));
        Assert.Empty(byUninstaller.Items);
        Assert.Contains($"Not offered: {vendor} is also where another installed app lives.", byUninstaller.NotOffered);
    }

    [Fact]
    public void AnEntryNamingARootAsItsHome_DoesNotMakeEverythingUnderItShared()
    {
        // Entries that give C:\Program Files as their install location exist; one of them must not stop every
        // install folder under Program Files from ever being offered.
        _env.IsElevated = true;
        var install = TempLeftoverEnvironment.Folder(ProgramFiles("Acme Notes"));
        _env.Registered.Add(new UninstallProbe("Badly Registered", "", "", _env.ProgramFiles));
        _env.Registered.Add(new UninstallProbe("Runtime", "", "", "",
            $"\"{Path.Combine(_env.WindowsDirectory, @"System32\rundll32.exe")}\" x.dll,Uninstall"));

        var findings = Find(Probe(installLocation: install));

        Assert.Equal(install, Assert.Single(findings.Items).Location);
    }

    [Fact]
    public void NamesThatSayNothing_AreNotSearchedFor()
    {
        var probe = Probe("1.0.9013", "Microsoft Corporation", "Vendor.bin", Path.Combine(_env.ProgramFiles, @"Acme\bin"));

        Assert.Empty(LeftoverFinder.AppNames(probe));
        Assert.Equal(["Vendor", "Acme"], LeftoverFinder.PublisherNames(probe, _env));
    }

    [Theory]
    [InlineData(@"Software\Acme\Notes", true)]
    [InlineData(@"Software\Acme\Notes"" ""C:\out.reg", false)]
    [InlineData(@"Software\Microsoft\Notes", false)]
    [InlineData(@"Software\Acme\Classes", false)]
    [InlineData(@"Software\Acme", false)]
    [InlineData(@"Software\Acme\Notes\Sub", false)]
    [InlineData(@"System\Acme\Notes", false)]
    public void OnlyAnAppsOwnKeyUnderSoftware_IsSafeToExportAndDelete(string key, bool safe)
        => Assert.Equal(safe, LeftoverFinder.IsSafeKeyPath(key));

    [Fact]
    public void AKeyNameHoldingANul_IsNotSafe()
    {
        // NUL ends a native command line, so everything after it would silently vanish from reg.exe's arguments.
        // Built here rather than passed as InlineData, where the character would ride into the test's own name.
        Assert.False(LeftoverFinder.IsSafeKeyPath("Software\\Acme\\No" + '\0' + "tes"));
    }

    [Fact]
    public void AFolderRefusedAsTheInstallFolder_IsMentionedOnce_WhenItsNameFindsItAgain()
    {
        var own = TempLeftoverEnvironment.Folder(_env.OwnFolders[0]);   // Local\SysManager, found again by its name

        var findings = Find(Probe("SysManager", "", "", own));

        Assert.Equal($"Not offered: {own} belongs to SysManager.", Assert.Single(findings.NotOffered));
    }

    // ── Sizes ─────────────────────────────────────────────────────────────

    [Fact]
    public void AFolderInsideAnotherListedFolder_IsCountedOnce()
    {
        var outer = new LeftoverItem { Location = Local("Discord"), Kind = LeftoverKind.Folder, Confidence = LeftoverConfidence.Probably, SizeBytes = 100 };
        var inner = new LeftoverItem { Location = Local(@"Discord\app-1.0"), Kind = LeftoverKind.Folder, Confidence = LeftoverConfidence.Certain, SizeBytes = 60 };
        var twin = new LeftoverItem { Location = Local("Discord"), Kind = LeftoverKind.Folder, Confidence = LeftoverConfidence.Guess, SizeBytes = 100 };
        var key = new LeftoverItem { Location = @"Software\Discord\Discord", Kind = LeftoverKind.RegistryKey, Confidence = LeftoverConfidence.OwnKey, SizeBytes = 5 };

        Assert.Equal(100, LeftoverFinder.DistinctBytes([inner, outer, twin, key]));
        Assert.Equal(60, LeftoverFinder.DistinctBytes([inner, key]));
    }
}
