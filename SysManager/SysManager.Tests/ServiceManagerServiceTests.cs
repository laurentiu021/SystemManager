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
    // ── Description resolution (#1582) ─────────────────────────────────────────

    [Fact]
    public void ResolveDescription_IndirectReference_ReturnsTheResolvedText()
    {
        var resolved = ServiceManagerService.ResolveDescription(
            @"@%SystemRoot%\system32\spoolsv.exe,-2",
            _ => "This service spools print jobs.");

        Assert.Equal("This service spools print jobs.", resolved);
    }

    [Theory]
    [InlineData("Transfers files in the background using idle network bandwidth.")]
    [InlineData("Provides user experience theme management.")]
    public void ResolveDescription_PlainText_IsReturnedUnchanged(string description)
    {
        // 57 services on a stock install store real text here, and it must survive byte-for-byte.
        Assert.Equal(
            description,
            ServiceManagerService.ResolveDescription(description, _ => "SHOULD NOT BE CALLED"));
    }

    [Fact]
    public void ResolveDescription_PlainText_NeverReachesTheNativeResolver()
    {
        // Not merely a performance point: handing arbitrary description text to a resource loader is
        // work that can only fail, and a resolver that returned something for plain text would
        // silently replace a real sentence.
        var calls = 0;
        ServiceManagerService.ResolveDescription(
            "Enables the detection of updates.",
            _ => { calls++; return "replaced"; });

        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveDescription_NoValue_IsEmpty(string? raw)
    {
        // 365 services carry no Description at all, so this is the most common input of the three.
        Assert.Equal("", ServiceManagerService.ResolveDescription(raw, _ => "SHOULD NOT BE CALLED"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ResolveDescription_FailedResolve_YieldsEmptyRatherThanTheRawReference(string? nativeResult)
    {
        // The defect this fix exists for was showing "@%SystemRoot%\system32\wuaueng.dll,-105" to the
        // user. Falling back to the raw value on failure would reintroduce exactly that string for the
        // 19 services whose binary or resource id cannot be resolved, and it would also put a DLL path
        // back into what the tab's free-text filter searches.
        var resolved = ServiceManagerService.ResolveDescription(
            @"@%SystemRoot%\system32\wuaueng.dll,-105",
            _ => nativeResult);

        Assert.Equal("", resolved);
    }

    [Fact]
    public void ResolveDescription_PassesTheWholeReferenceToTheResolver()
    {
        // The '@' is part of the indirect-string syntax the native API parses; stripping it would make
        // every resolution fail while still looking plausible at the call site.
        string? seen = null;
        const string reference = @"@%SystemRoot%\system32\spoolsv.exe,-2";

        ServiceManagerService.ResolveDescription(reference, source => { seen = source; return "text"; });

        Assert.Equal(reference, seen);
    }
}
