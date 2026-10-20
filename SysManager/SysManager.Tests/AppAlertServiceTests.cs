// SysManager · AppAlertServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// <see cref="AppAlertService.CheckNow"/>, the check F5 runs on New App Alerts. The installed-programs list
/// comes from a delegate, so each test decides what was installed before and after the baseline.
/// </summary>
public class AppAlertServiceTests
{
    private static AppInstallEntry App(string name) => new() { Name = name, Source = "Registry" };

    [Fact]
    public void CheckNow_BeforeMonitoringStarts_AnnouncesNothing()
    {
        // With no baseline, every installed program is missing from the known list, so a check here would
        // announce all of them as new.
        using var service = new AppAlertService(() => [App("Already installed"), App("Also installed")]);

        Assert.Equal(0, service.CheckNow());
    }

    [Fact]
    public void CheckNow_WhileMonitoring_CountsOnlyWhatIsNewSinceTheBaseline()
    {
        List<AppInstallEntry> installed = [App("Already installed")];
        using var service = new AppAlertService(() => [.. installed]);
        service.TakeBaseline();
        service.Start();

        Assert.Equal(0, service.CheckNow());

        installed.Add(App("Installed afterwards"));
        Assert.Equal(1, service.CheckNow());

        // Announced once: a later check, or the timer's own pass, does not report it again.
        Assert.Equal(0, service.CheckNow());
    }

    [Fact]
    public void CheckNow_AfterMonitoringStops_AnnouncesNothing()
    {
        List<AppInstallEntry> installed = [App("Already installed")];
        using var service = new AppAlertService(() => [.. installed]);
        service.TakeBaseline();
        service.Start();
        service.Stop();

        installed.Add(App("Installed afterwards"));

        Assert.Equal(0, service.CheckNow());
    }

    // ---------- one install seen by both detectors (#2878) ----------
    // An installer creates its folder first and writes its uninstall entry minutes later, and the two detectors were
    // never linked: VLC came up as "VideoLAN" and as "VLC media player", two installs.

    // A folder no PC has, so the baseline of the real Program Files never holds it.
    private const string Folder = @"C:\Program Files\Contoso Media 2878";

    /// <summary>A clock that says what the test last set, in a time zone with no offset, so local time is that too.</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTime Now { get; set; } = new(2026, 10, 20, 15, 0, 0);

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>A monitoring service over <paramref name="installed"/>, and what it announced.</summary>
    private static (AppAlertService Service, List<AppInstallEntry> Detected, List<(AppInstallEntry Alert, AppInstallEntry App)> Identified)
        Watching(List<AppInstallEntry> installed, ManualClock clock)
    {
        var service = new AppAlertService(() => [.. installed], clock);
        var detected = new List<AppInstallEntry>();
        var identified = new List<(AppInstallEntry, AppInstallEntry)>();
        service.NewAppDetected += detected.Add;
        service.AppIdentified += (alert, app) => identified.Add((alert, app));
        service.TakeBaseline();
        service.Start();
        return (service, detected, identified);
    }

    [Fact]
    public void AnInstallSeenByBothDetectors_IsAnnouncedOnce_ThenNamedAfterTheProgram()
    {
        List<AppInstallEntry> installed = [App("Already installed")];
        var clock = new ManualClock();
        var (service, detected, identified) = Watching(installed, clock);
        using var _ = service;

        service.AnnounceFolder(Folder);
        clock.Now = clock.Now.AddMinutes(3);
        var player = new AppInstallEntry
        {
            Name = "Contoso Player",
            Publisher = "Contoso",
            Source = "Registry",
            InstallPath = Folder + @"\Player"
        };
        installed.Add(player);

        Assert.Equal(1, service.CheckNow());

        var folderAlert = Assert.Single(detected, d => d.InstallPath == Folder);
        Assert.Equal(("Contoso Media 2878", "FileSystem"), (folderAlert.Name, folderAlert.Source));
        Assert.DoesNotContain(detected, d => d.Name == "Contoso Player");
        var (alert, app) = Assert.Single(identified);
        Assert.Same(folderAlert, alert);
        Assert.Same(player, app);
    }

    [Fact]
    public void AnEntryWithNoInstallLocation_ClaimsTheFolderItsUninstallerIsIn()
    {
        List<AppInstallEntry> installed = [];
        var clock = new ManualClock();
        var (service, detected, identified) = Watching(installed, clock);
        using var _ = service;

        service.AnnounceFolder(Folder);
        installed.Add(new AppInstallEntry
        {
            Name = "Contoso Player",
            Source = "Registry",
            Locations = AppAlertService.FoldersOf("\"" + Folder + @"\Player\uninstall.exe"" /S", null),
        });

        service.CheckNow();

        Assert.Single(detected);
        Assert.Equal("Contoso Player", Assert.Single(identified).App.Name);
    }

    [Fact]
    public void AnEntryOutsideTheFolder_IsAnInstallOfItsOwn()
    {
        List<AppInstallEntry> installed = [];
        var clock = new ManualClock();
        var (service, detected, identified) = Watching(installed, clock);
        using var _ = service;

        service.AnnounceFolder(Folder);
        // A sibling whose name starts like the folder's is not inside it.
        installed.Add(new AppInstallEntry { Name = "Contoso Tools", Source = "Registry", InstallPath = Folder + " Tools" });

        service.CheckNow();

        Assert.Equal([Folder, Folder + " Tools"], detected.Select(d => d.InstallPath));
        Assert.Empty(identified);
    }

    [Fact]
    public void AFolderAnnouncedLongerAgoThanOneInstallTakes_IsNotClaimed()
    {
        List<AppInstallEntry> installed = [];
        var clock = new ManualClock();
        var (service, detected, identified) = Watching(installed, clock);
        using var _ = service;

        service.AnnounceFolder(Folder);
        clock.Now = clock.Now + RecentChangesService.SameInstall + TimeSpan.FromSeconds(1);
        installed.Add(new AppInstallEntry { Name = "Contoso Player", Source = "Registry", InstallPath = Folder });

        service.CheckNow();

        Assert.Equal(2, detected.Count);
        Assert.Empty(identified);
    }

    [Fact]
    public void AFolderIsClaimedOnce_SoASecondEntryInsideItIsAnInstallOfItsOwn()
    {
        List<AppInstallEntry> installed = [];
        var clock = new ManualClock();
        var (service, detected, identified) = Watching(installed, clock);
        using var _ = service;

        service.AnnounceFolder(Folder);
        installed.Add(new AppInstallEntry { Name = "Contoso Player", Source = "Registry", InstallPath = Folder + @"\Player" });
        service.CheckNow();
        installed.Add(new AppInstallEntry { Name = "Contoso Codecs", Source = "Registry", InstallPath = Folder + @"\Codecs" });
        service.CheckNow();

        Assert.Equal("Contoso Player", Assert.Single(identified).App.Name);
        Assert.Equal([Folder, Folder + @"\Codecs"], detected.Select(d => d.InstallPath));
    }

    [Fact]
    public void AnEntryFoundWhileItsFolderIsBeingAnnounced_DoesNotClaimIt()
    {
        // The folder waits for its entry only once it is raised, so a renamed alert has always been listed first.
        List<AppInstallEntry> installed = [];
        var clock = new ManualClock();
        var (service, _, identified) = Watching(installed, clock);
        using var watching = service;
        installed.Add(new AppInstallEntry { Name = "Contoso Player", Source = "Registry", InstallPath = Folder });
        var claimedMeanwhile = -1;
        service.NewAppDetected += d =>
        {
            if (d.Source == "FileSystem")
            {
                service.CheckNow();
                claimedMeanwhile = identified.Count;
            }
        };

        service.AnnounceFolder(Folder);

        Assert.Equal(0, claimedMeanwhile);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Contoso\uninstall.exe"" /S", null, @"C:\Program Files\Contoso")]
    [InlineData(@"C:\Program Files\Contoso Notes\unins000.exe", null, @"C:\Program Files\Contoso Notes")]
    [InlineData(null, @"C:\Program Files\Contoso\bin\contoso.exe,0", @"C:\Program Files\Contoso\bin")]
    [InlineData(null, @"""C:\Program Files\Contoso\contoso.exe"",-101", @"C:\Program Files\Contoso")]
    public void FoldersOf_ReadsTheUninstallersAndTheIconsFolder(string? command, string? icon, string expected)
    {
        Assert.Equal([expected], AppAlertService.FoldersOf(command, icon));
    }

    [Theory]
    [InlineData("MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", null)]
    [InlineData("rundll32.exe dfshim.dll,ShArpMaintain Contoso.application", "contoso.ico")]
    [InlineData("", "  ")]
    [InlineData(null, null)]
    public void FoldersOf_NamesNoFolder_ForACommandOrIconWithNoPathOfItsOwn(string? command, string? icon)
    {
        Assert.Empty(AppAlertService.FoldersOf(command, icon));
    }
}
