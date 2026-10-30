// SysManager · DefenderServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class DefenderServiceTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("True", true)]
    [InlineData("False", false)]
    [InlineData(null, false)]
    public void ToBool_HandlesBoolAndStringAndNull(object? input, bool expected)
        => Assert.Equal(expected, DefenderService.ToBool(input));

    [Theory]
    [InlineData(2, 2)]
    [InlineData("1", 1)]
    [InlineData(null, 0)]
    [InlineData("garbage", 0)]
    public void ToInt_ParsesOrZero(object? input, int expected)
        => Assert.Equal(expected, DefenderService.ToInt(input));

    [Fact]
    public void ToStringList_FromArray()
    {
        var list = DefenderService.ToStringList(new object[] { @"C:\Games", @"D:\Steam", "" });
        Assert.Equal(2, list.Count);
        Assert.Contains(@"C:\Games", list);
        Assert.DoesNotContain("", list);
    }

    [Fact]
    public void ToStringList_FromSingleAndNull()
    {
        Assert.Single(DefenderService.ToStringList(@"C:\One"));
        Assert.Empty(DefenderService.ToStringList(null));
    }

    [Fact]
    public void ToStringList_FromRemotingCollectionShape()
    {
        var deserialized = new PSObject(new ArrayList
        {
            new PSObject(@"C:\Games"),
            new PSObject(@"D:\Steam"),
            new PSObject("")
        });

        var list = DefenderService.ToStringList(deserialized);

        Assert.Equal(new[] { @"C:\Games", @"D:\Steam" }, list);
    }

    [Fact]
    public async Task MutationScripts_DeclareAndBindTheirParameters()
    {
        const string path = @"C:\Games";
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Collection<PSObject>());
        var service = new DefenderService(runner);

        await service.SetPuaProtectionAsync(1);
        await service.SetControlledFolderAccessAsync(2);
        await service.AddExclusionPathAsync(path);
        await service.RemoveExclusionPathAsync(path);

        await runner.Received(1).RunAsync(
            "param([int]$Value) Set-MpPreference -PUAProtection $Value",
            Arg.Is<IDictionary<string, object?>?>(values =>
                values != null && Equals(values["Value"], 1)),
            Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(
            "param([int]$Value) Set-MpPreference -EnableControlledFolderAccess $Value",
            Arg.Is<IDictionary<string, object?>?>(values =>
                values != null && Equals(values["Value"], 2)),
            Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(
            "param([string]$Path) Add-MpPreference -ExclusionPath $Path",
            Arg.Is<IDictionary<string, object?>?>(values =>
                values != null && Equals(values["Path"], path)),
            Arg.Any<CancellationToken>());
        await runner.Received(1).RunAsync(
            "param([string]$Path) Remove-MpPreference -ExclusionPath $Path",
            Arg.Is<IDictionary<string, object?>?>(values =>
                values != null && Equals(values["Path"], path)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]   // out of range → clamp to 0
    [InlineData(-1, 0)]
    public void ClampTri_KeepsZeroToTwo(int input, int expected)
        => Assert.Equal(expected, DefenderService.ClampTri(input));

    // ── Exclusion-path validation at the service boundary (idx 151) ───────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\path")]   // not rooted
    [InlineData(@"C:\Games\*")]       // wildcard
    [InlineData(@"C:\Games\?")]       // wildcard
    public void IsValidExclusionPath_RejectsBadInput(string? path)
        => Assert.False(DefenderService.IsValidExclusionPath(path!));

    [Theory]
    [InlineData(@"C:\Games")]
    [InlineData(@"D:\Steam\steamapps")]
    public void IsValidExclusionPath_AcceptsRootedNonWildcardPath(string path)
        => Assert.True(DefenderService.IsValidExclusionPath(path));

    // ── an exclusion can never cover a whole drive, Windows or the user's own files (#2773) ──

    /// <summary>A machine of the test's own: the folders the rule protects, under made-up roots.</summary>
    private static readonly IReadOnlyList<(string Name, string? Path, bool ContentsToo)> Protected =
    [
        ("the Windows folder", @"C:\Windows", true),
        ("Program Files", @"C:\Program Files", false),
        ("the folder every account's files are in", @"C:\Users", false),
        ("your account's folder", @"C:\Users\me", false),
        ("Downloads", @"D:\Downloads", false),
        ("your temp folder", @"C:\Users\me\AppData\Local\Temp", false),
    ];

    [Theory]
    [InlineData(@"C:\", "is a whole drive or network share")]
    [InlineData(@"D:\", "is a whole drive or network share")]
    [InlineData(@"\\server\share\", "is a whole drive or network share")]
    [InlineData(@"C:", "is not a full folder path without wildcards")]           // rooted, but the current directory on C:
    [InlineData(@"\Windows", "is not a full folder path without wildcards")]     // rooted, but on the current drive
    [InlineData(@"C:\Windows", "is or holds the Windows folder")]
    [InlineData(@"C:\Windows\System32", "is inside the Windows folder")]
    [InlineData(@"C:\Program Files", "is or holds Program Files")]
    [InlineData(@"C:\Users", "is or holds the folder every account's files are in")]
    [InlineData(@"C:\Users\me", "is or holds your account's folder")]
    [InlineData(@"C:\Users\me\AppData", "is or holds your temp folder")]
    [InlineData(@"D:\Downloads\", "is or holds Downloads")]
    public void ExclusionRefusal_RefusesWhatWouldLeaveTheSystemOrTheUsersFilesUnscanned(string path, string why)
        => Assert.Equal(why, DefenderService.ExclusionRefusal(path, Protected));

    /// <summary>The other side: a folder inside the ones that are not system folders is what exclusions are for.</summary>
    [Theory]
    [InlineData(@"C:\Games")]
    [InlineData(@"C:\Program Files\Steam\steamapps")]
    [InlineData(@"C:\Users\me\Documents\Builds")]
    [InlineData(@"\\server\share\Builds")]
    public void ExclusionRefusal_AllowsAFolderInsideThem(string path)
        => Assert.Null(DefenderService.ExclusionRefusal(path, Protected));

    /// <summary>The real folders of the machine running the suite, through the overload the service itself uses.</summary>
    [Fact]
    public void IsValidExclusionPath_RefusesThisMachinesDriveWindowsAndProfile()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(windows) || string.IsNullOrEmpty(profile));   // else nothing below is real

        Assert.False(DefenderService.IsValidExclusionPath(Path.GetPathRoot(windows)!));
        Assert.False(DefenderService.IsValidExclusionPath(windows));
        Assert.False(DefenderService.IsValidExclusionPath(Path.Join(windows, "System32")));
        Assert.False(DefenderService.IsValidExclusionPath(profile));
        Assert.False(DefenderService.IsValidExclusionPath(SysManager.Helpers.KnownFolders.GetDownloadsPath()));
    }

    [Fact]
    public async Task AddExclusionPathAsync_ForAWholeDrive_Throws_AndRunsNothing()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var service = new DefenderService(runner);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => service.AddExclusionPathAsync(@"C:\"));

        Assert.Contains("is a whole drive or network share", refused.Message, StringComparison.Ordinal);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
    }

    [Fact]
    public void ParseStatus_NormalizesInvertedRealtimeBoolean()
    {
        // DisableRealtimeMonitoring = true means real-time protection is OFF.
        var obj = new PSObject();
        obj.Properties.Add(new PSNoteProperty("DisableRealtimeMonitoring", true));
        obj.Properties.Add(new PSNoteProperty("PUAProtection", 1));
        obj.Properties.Add(new PSNoteProperty("MAPSReporting", 2));
        obj.Properties.Add(new PSNoteProperty("EnableControlledFolderAccess", 0));
        obj.Properties.Add(new PSNoteProperty("ExclusionPath", new object[] { @"C:\Games" }));
        obj.Properties.Add(new PSNoteProperty("ExclusionExtension", new object[] { }));
        obj.Properties.Add(new PSNoteProperty("ExclusionProcess", new object[] { }));
        obj.Properties.Add(new PSNoteProperty("IsTamperProtected", true));

        var status = DefenderService.ParseStatus(obj);

        Assert.True(status.Available);
        Assert.False(status.RealtimeProtection); // inverted: Disable=true → protection OFF
        Assert.True(status.IsTamperProtected);
        Assert.Equal(1, status.PuaProtection);
        Assert.Equal(2, status.MapsReporting);
        Assert.Equal(0, status.ControlledFolderAccess);
        Assert.Single(status.ExclusionPaths);
    }

    [Fact]
    public void ParseStatus_RealtimeOn_WhenDisableFalse()
    {
        var obj = new PSObject();
        obj.Properties.Add(new PSNoteProperty("DisableRealtimeMonitoring", false));
        var status = DefenderService.ParseStatus(obj);
        Assert.True(status.RealtimeProtection);
    }

    // ── Real-time protection as it runs, not only as it is set (#2867) ────────
    // The card read the setting, so with another antivirus in charge it said "On" while Defender was not scanning.

    private static DefenderStatus Realtime(bool setting, object? inEffect, string? mode)
    {
        var obj = new PSObject();
        obj.Properties.Add(new PSNoteProperty("DisableRealtimeMonitoring", !setting));
        if (inEffect is not null) obj.Properties.Add(new PSNoteProperty("RealTimeProtectionEnabled", inEffect));
        if (mode is not null) obj.Properties.Add(new PSNoteProperty("AMRunningMode", mode));
        return DefenderService.ParseStatus(obj);
    }

    [Theory]
    [InlineData("Passive Mode")]
    [InlineData("SxS Passive Mode")]
    [InlineData("EDR Block Mode")]
    public void WithAnotherAntivirusInCharge_RealtimeReadsOff_AndSaysWhy(string mode)
    {
        var status = Realtime(setting: true, inEffect: false, mode);

        Assert.True(status.RealtimeProtection);   // the premise: the setting is on
        Assert.Equal("Off", status.RealtimeDisplay);
        Assert.Equal("Real-time protection is off: another antivirus is in charge, and Windows runs Defender in passive mode.",
            status.RealtimeNote);
    }

    [Fact]
    public void RealtimeOffForAnotherReason_ReadsOff_AndSaysTheSettingIsOn()
    {
        var status = Realtime(setting: true, inEffect: false, "Normal");

        Assert.Equal("Off", status.RealtimeDisplay);
        Assert.Equal("Real-time protection is off, although its setting here is on.", status.RealtimeNote);
    }

    [Theory]
    [InlineData(true, true, "On")]
    [InlineData(false, false, "Off")]
    [InlineData(false, true, "On")]   // turned on outside this setting: what runs is what counts
    [InlineData(true, null, "On")]    // not read: the setting is all there is
    [InlineData(false, null, "Off")]
    public void WhenTheTwoAgree_OrTheStateInEffectIsUnknown_NothingNeedsSaying(bool setting, bool? inEffect, string shown)
    {
        var status = Realtime(setting, inEffect, inEffect is null ? null : "Normal");

        Assert.Equal(shown, status.RealtimeDisplay);
        Assert.Equal("", status.RealtimeNote);
        Assert.Equal(inEffect, status.RealtimeInEffect);
    }

    // ── The status read stops on a failed read (#2476) ──────────────────────
    // A read that failed and returned normally left its variable null, the object was still emitted with every
    // field null, and ParseStatus turns a null "Disable" flag into protection ON. So a status that was never read
    // showed real-time protection "On".

    [Fact]
    public void StatusScript_StopsWhenEitherReadFails()
    {
        // The shape that does the work: -ErrorAction Stop on each read itself.
        var lines = DefenderService.StatusScript.Split('\n').Select(line => line.Trim()).ToList();

        Assert.Contains("$p = Get-MpPreference -ErrorAction Stop", lines);
        Assert.Contains("$s = Get-MpComputerStatus -ErrorAction Stop", lines);
    }

    [Theory]
    [InlineData("Get-MpPreference")]
    [InlineData("Get-MpComputerStatus")]
    public void StatusScript_WhenAReadFails_Throws(string failingRead)
    {
        // The exact text, against the two cmdlets shadowed by functions. The failing one reports a non-terminating
        // error, the way the real cmdlet reports a Defender service that is not running.
        Assert.ThrowsAny<RuntimeException>(() => RunStatusScriptAgainstStubs(failingRead));
    }

    [Fact]
    public void StatusScript_WhenBothReadsSucceed_ProjectsTheStatus()
    {
        // The positive control: a script that threw on everything would pass the case above.
        var status = DefenderService.ParseStatus(Assert.Single(RunStatusScriptAgainstStubs(failingRead: null)));

        Assert.False(status.RealtimeProtection);   // the stub disables it, so a null would read as ON
        Assert.True(status.IsTamperProtected);
        Assert.Equal(new[] { @"C:\Games" }, status.ExclusionPaths);
        Assert.Equal((false, "Passive Mode"), (status.RealtimeInEffect, status.RunningMode));
    }

    /// <summary>
    /// Runs <see cref="DefenderService.StatusScript"/> behind stub functions for both reads.
    /// </summary>
    /// <remarks>
    /// In process, and the stubs call nothing from a module: the failing one writes through
    /// <c>$PSCmdlet.WriteError</c> rather than <c>Write-Error</c>, which lives in <c>Microsoft.PowerShell.Utility</c>
    /// and is not loaded here. A function takes precedence over a cmdlet of the same name, so Defender is never read.
    /// </remarks>
    private static Collection<PSObject> RunStatusScriptAgainstStubs(string? failingRead)
    {
        const string preference =
            "function Get-MpPreference { [CmdletBinding()] param() [pscustomobject]@{ DisableRealtimeMonitoring = $true; " +
            "PUAProtection = 1; MAPSReporting = 2; EnableControlledFolderAccess = 0; ExclusionPath = @('C:\\Games'); " +
            "ExclusionExtension = @(); ExclusionProcess = @() } }\n";
        const string computerStatus =
            "function Get-MpComputerStatus { [CmdletBinding()] param() [pscustomobject]@{ IsTamperProtected = $true; " +
            "RealTimeProtectionEnabled = $false; AMRunningMode = 'Passive Mode' } }\n";

        static string Failing(string name) =>
            $"function {name} {{ [CmdletBinding()] param() $PSCmdlet.WriteError([System.Management.Automation.ErrorRecord]::new(" +
            "[System.InvalidOperationException]::new('The service is not running.'), 'NotRunning', 'NotSpecified', $null)) }\n";

        using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(
            (failingRead == "Get-MpPreference" ? Failing("Get-MpPreference") : preference) +
            (failingRead == "Get-MpComputerStatus" ? Failing("Get-MpComputerStatus") : computerStatus) +
            DefenderService.StatusScript);
        return ps.Invoke();
    }

    // ── Exclusions Windows withholds from a standard user (#2476) ───────────
    // Windows puts this sentence (as an English system words it) where each list should be. Listed as an
    // excluded folder, it would be a claim about the machine that nobody had read.
    private const string Withheld = "N/A: Must be an administrator to view exclusions";

    [Fact]
    public void HideWithheldExclusions_ForAStandardUser_EmptiesTheListsAndMarksThemUnreadable()
    {
        var status = DefenderStatus.Unavailable with
        {
            Available = true,
            ExclusionPaths = [Withheld],
            ExclusionExtensions = [Withheld],
            ExclusionProcesses = [Withheld],
        };

        var shown = DefenderService.HideWithheldExclusions(status, elevated: false);

        Assert.Empty(shown.ExclusionPaths);
        Assert.Empty(shown.ExclusionExtensions);
        Assert.Empty(shown.ExclusionProcesses);
        Assert.False(shown.ExclusionsReadable);
        Assert.True(shown.Available);   // the rest of the status was read, and still stands
    }

    [Fact]
    public void HideWithheldExclusions_ForAnAdministrator_KeepsTheLists()
    {
        var status = DefenderStatus.Unavailable with { Available = true, ExclusionPaths = [@"C:\Games"] };

        var shown = DefenderService.HideWithheldExclusions(status, elevated: true);

        Assert.Equal(new[] { @"C:\Games" }, shown.ExclusionPaths);
        Assert.True(shown.ExclusionsReadable);
    }
}
