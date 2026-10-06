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
}
