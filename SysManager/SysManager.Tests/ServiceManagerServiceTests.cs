// SysManager · ServiceManagerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class ServiceManagerServiceTests
{
    [Fact]
    public void GetAllServices_ReturnsNonEmptyList()
    {
        var services = ServiceManagerService.GetAllServices();
        Assert.NotEmpty(services);
    }

    [Fact]
    public void GetAllServices_SortedByDisplayName()
    {
        var services = ServiceManagerService.GetAllServices();
        for (int i = 1; i < services.Count; i++)
            Assert.True(
                string.Compare(services[i - 1].DisplayName, services[i].DisplayName,
                    StringComparison.OrdinalIgnoreCase) <= 0,
                $"Not sorted: '{services[i - 1].DisplayName}' > '{services[i].DisplayName}'");
    }

    [Fact]
    public void GetAllServices_HasNameAndDisplayName()
    {
        var services = ServiceManagerService.GetAllServices();
        foreach (var s in services.Take(10))
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.False(string.IsNullOrWhiteSpace(s.DisplayName));
        }
    }

    [Fact]
    public void GamingGuide_ContainsSysMain()
    {
        Assert.True(ServiceManagerService.GamingGuide.ContainsKey("SysMain"));
        Assert.Equal("safe-to-disable", ServiceManagerService.GamingGuide["SysMain"].Rec);
    }

    [Fact]
    public void GamingGuide_CaseInsensitive()
    {
        Assert.True(ServiceManagerService.GamingGuide.ContainsKey("sysmain"));
        Assert.True(ServiceManagerService.GamingGuide.ContainsKey("SYSMAIN"));
    }

    [Fact]
    public void GamingGuide_XboxServicesAreAdvanced()
    {
        foreach (var name in new[] { "XblAuthManager", "XblGameSave", "XboxGipSvc", "XboxNetApiSvc" })
        {
            Assert.True(ServiceManagerService.GamingGuide.ContainsKey(name));
            Assert.Equal("advanced", ServiceManagerService.GamingGuide[name].Rec);
        }
    }

    [Fact]
    public void RefreshStatus_KnownService()
    {
        var entry = new ServiceEntry { Name = "Winmgmt" };
        ServiceManagerService.RefreshStatus(entry);
        Assert.False(string.IsNullOrWhiteSpace(entry.Status));
    }

    [Fact]
    public void RefreshStatus_UnknownService_SetsUnknown()
    {
        var entry = new ServiceEntry { Name = "NonExistentService12345" };
        ServiceManagerService.RefreshStatus(entry);
        Assert.Equal("Unknown", entry.Status);
    }

    [Fact]
    public void ServiceEntry_ObservableProperties()
    {
        var entry = new ServiceEntry { Name = "Test" };
        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        entry.Status = "Running";
        entry.StartType = "Automatic";
        Assert.Contains("Status", changed);
        Assert.Contains("StartType", changed);
    }
}
