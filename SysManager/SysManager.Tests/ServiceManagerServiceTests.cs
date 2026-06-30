// SysManager · ServiceManagerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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

    // ── StartTypeToScToken (regression: enable restores the previous start type) ──

    [Theory]
    [InlineData("Automatic", "auto")]
    [InlineData("Manual", "demand")]
    [InlineData("Boot", "boot")]
    [InlineData("System", "system")]
    public void StartTypeToScToken_MapsKnownStartTypes(string startType, string expected)
        => Assert.Equal(expected, ServiceManagerService.StartTypeToScToken(startType));

    [Theory]
    [InlineData("Disabled")]   // re-enabling to Disabled is a no-op → fall back to Manual
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Weird")]
    public void StartTypeToScToken_FallsBackToDemand_ForDisabledOrUnknown(string? startType)
        => Assert.Equal("demand", ServiceManagerService.StartTypeToScToken(startType));

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

    // ── SetStartupTypeAsync input validation (idx 174 — negative tests) ───────
    // The validation throws BEFORE sc.exe is ever launched, so a real runner can be
    // passed safely: these rejection paths never spawn a process.

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad;name")]      // command separator
    [InlineData("name&calc")]     // command chaining
    [InlineData("name|pipe")]
    [InlineData("name\"quote")]
    [InlineData("name\nnewline")]
    public async Task SetStartupTypeAsync_InvalidServiceName_Throws(string serviceName)
    {
        var ps = new PowerShellRunner();
        await Assert.ThrowsAsync<ArgumentException>(
            () => ServiceManagerService.SetStartupTypeAsync(serviceName, "demand", ps));
    }

    [Theory]
    [InlineData("totally-bogus")]
    [InlineData("AUTOMATIC")]   // the sc.exe token is "auto", not the .NET name
    [InlineData("")]
    [InlineData("enabled")]
    public async Task SetStartupTypeAsync_InvalidStartType_Throws(string startType)
    {
        // Valid service name, invalid start type → rejected before sc.exe is launched.
        // (We deliberately never call it with a VALID type here — that would spawn
        // sc.exe and mutate a real service.)
        var ps = new PowerShellRunner();
        await Assert.ThrowsAsync<ArgumentException>(
            () => ServiceManagerService.SetStartupTypeAsync("Winmgmt", startType, ps));
    }
}
