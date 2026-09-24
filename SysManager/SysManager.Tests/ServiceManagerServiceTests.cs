// SysManager · ServiceManagerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
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
    public async Task StopServiceAsync_ForAServiceWindowsWillNotStop_SaysSoInsteadOfReturning()
    {
        // #2431. A running service that accepts no stop request used to be skipped and the call returned as
        // if it had stopped, so the tab said "✓ stopped" over a service still running. RpcSs is running on
        // every Windows install and never accepts a stop, so the refusal is reached without elevation and
        // without anything being stopped: the check comes before any stop request is sent.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ServiceManagerService.StopServiceAsync("RpcSs"));

        Assert.Contains("does not accept a stop request", ex.Message, StringComparison.Ordinal);
    }

    // ── StartTypeToScToken (regression: enable restores the previous start type) ──

    [Theory]
    [InlineData("Automatic", "auto")]
    [InlineData("Manual", "demand")]
    // services.msc's name for it. ServiceStartMode has no delayed member, so before #2428 a delayed service
    // was recorded as plain Automatic and restored with start= auto.
    [InlineData("Automatic (Delayed Start)", "delayed-auto")]
    // The ledger has always accepted a type case-insensitively; the mapping has to agree, or a record the
    // ledger keeps still falls through to Manual.
    [InlineData("automatic", "auto")]
    public void StartTypeToScToken_MapsKnownStartTypes(string startType, string expected)
        => Assert.Equal(expected, ServiceManagerService.StartTypeToScToken(startType));

    [Theory]
    [InlineData("Disabled")]   // re-enabling to Disabled is a no-op → fall back to Manual
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Weird")]
    // Driver start types. This tab lists no drivers, and SetStartupTypeAsync refuses "boot" and "system", so
    // mapping to them could only ever end in an exception instead of an enabled service.
    [InlineData("Boot")]
    [InlineData("System")]
    public void StartTypeToScToken_FallsBackToDemand_ForDisabledOrUnknown(string? startType)
        => Assert.Equal("demand", ServiceManagerService.StartTypeToScToken(startType));

    [Fact]
    public void ServiceEntry_ObservableProperties()
    {
        var entry = new ServiceEntry { Name = "Test" };
        var changed = entry.RecordPropertyChanges();
        entry.Status = "Running";
        entry.StartType = "Automatic";
        entry.IsDelayedAutoStart = true;
        Assert.Contains("Status", changed);
        Assert.Contains("StartType", changed);
        // RefreshStatus rewrites it on a row the grid is showing, alongside StartType.
        Assert.Contains("IsDelayedAutoStart", changed);
    }

    // ── The delayed-start flag (#2428) ─────────────────────────────────────────

    [Theory]
    [InlineData("Automatic", true, "Automatic (Delayed Start)")]
    [InlineData("Automatic", false, "Automatic")]
    // Windows keeps the delay setting on other start types and ignores it there, so a flag that somehow
    // reached a Manual or Disabled row must not relabel it.
    [InlineData("Manual", true, "Manual")]
    [InlineData("Disabled", true, "Disabled")]
    [InlineData("Manual", false, "Manual")]
    public void StartTypeWithDelay_NamesTheDelayOnlyOnAnAutomaticService(string startType, bool delayed, string expected)
    {
        var entry = new ServiceEntry { Name = "svc", StartType = startType, IsDelayedAutoStart = delayed };

        Assert.Equal(expected, ServiceManagerService.StartTypeWithDelay(entry));
    }

    [Fact]
    public void StartTypeWithDelay_ProducesTheNameTheRestorePathMaps()
    {
        // The snapshot and the mapping have to agree on the spelling, or Disable records a name Enable
        // cannot restore — which is this bug again, one step further along.
        var entry = new ServiceEntry { Name = "svc", StartType = "Automatic", IsDelayedAutoStart = true };

        Assert.Equal("delayed-auto",
            ServiceManagerService.StartTypeToScToken(ServiceManagerService.StartTypeWithDelay(entry)));
    }

    [Fact]
    public void ReadDelayedAutoStart_ForAServiceThatDoesNotExist_IsUnknownRatherThanFalse()
    {
        // Null, not false: "Windows said it is not delayed" and "Windows could not be asked" are different
        // answers, even though the scan treats both as not delayed.
        Assert.Null(ServiceManagerService.ReadDelayedAutoStart("NonExistentService12345"));
    }

    [Fact]
    public void ReadDelayedAutoStart_ForRemoteProcedureCall_GetsAnAnswerFromWindows()
    {
        // The positive half of the plumbing. RpcSs exists on every Windows install, starts automatically and
        // is never delayed — everything else waits on it. A wrong entry point, struct size or access right
        // makes every query fail, which the scan would silently read as "not delayed" on every row, so this
        // asserts an ANSWER came back, not merely that nothing threw.
        Assert.False(ServiceManagerService.ReadDelayedAutoStart("RpcSs"));
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

    // ── What the Services commands ask before a startup change (#2430, #2432) ──

    [Theory]
    [InlineData("Winmgmt")]
    [InlineData("Windows Search")]
    [InlineData("MSSQL$SQLEXPRESS")]      // instance names carry a dollar sign
    [InlineData("cbdhsvc_243f95")]        // a per-user service instance
    [InlineData("a.b-c_d")]
    public void IsSafeForScExe_AcceptsTheNamesTheCommandLineCheckAllows(string name)
        => Assert.True(ServiceManagerService.IsSafeForScExe(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad;name")]
    [InlineData("name&calc")]
    [InlineData("name\"quote")]
    [InlineData("name\nnewline")]
    // Legal in Windows — only / and \ are not — and still refused: the check is deliberately narrower than
    // Windows, because the name goes on a command line. Refusing one must end in words, not the crash dialog.
    [InlineData("Vendor(R) Updater")]
    public void IsSafeForScExe_RefusesEverythingSetStartupTypeAsyncWouldReject(string? name)
    {
        Assert.False(ServiceManagerService.IsSafeForScExe(name));
    }

    [Theory]
    [InlineData("Winmgmt", true)]
    [InlineData("MSSQL$SQLEXPRESS", true)]
    [InlineData("Vendor(R) Updater", false)]
    [InlineData("bad;name", false)]
    public async Task SetStartupTypeAsync_RefusesExactlyWhatIsSafeForScExeRefuses(string name, bool safe)
    {
        // One check, asked in two places: the commands ask it before prompting and SetStartupTypeAsync enforces
        // it. If they ever disagreed, a name the commands let through would crash again, or one they refused
        // would have been fine. A substitute rather than the real runner, so an accepted name reaches nothing.
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(0);

        var thrown = await Record.ExceptionAsync(
            () => ServiceManagerService.SetStartupTypeAsync(name, "demand", runner));

        Assert.Equal(safe, ServiceManagerService.IsSafeForScExe(name));
        Assert.Equal(!safe, thrown is ArgumentException);
        await runner.Received(safe ? 1 : 0).RunProcessAsync(
            "sc.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    // Case-insensitive, as RehydratePreviousStartTypes always compared. Nothing produces "disabled" today, and
    // a rule that depended on the casing would be one more thing to keep true.
    [Theory]
    [InlineData("Disabled", true)]
    [InlineData("disabled", true)]
    [InlineData("Manual", false)]
    [InlineData("Automatic", false)]
    [InlineData("", false)]
    public void IsDisabled_ReadsTheStartType(string startType, bool expected)
    {
        Assert.Equal(expected, ServiceManagerService.IsDisabled(new ServiceEntry { Name = "svc", StartType = startType }));
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
