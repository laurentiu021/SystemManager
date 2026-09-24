// SysManager · PerformanceServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PerformanceService"/>. Focuses on parsing logic
/// since powercfg/registry calls are integration-level.
/// </summary>
public class PerformanceServiceTests
{
    private static PerformanceService NewService(IPowerShellRunner runner) =>
        new(runner, new RestorePointService(runner));

    // ── ParseActivePlan ──

    [Fact]
    public void ParseActivePlan_ValidOutput_ReturnsNameAndGuid()
    {
        var lines = new List<string>
        {
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)"
        };

        var (name, guid) = PerformanceService.ParseActivePlan(lines);

        Assert.Equal("Balanced", name);
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", guid);
    }

    [Fact]
    public void ParseActivePlan_UltimatePerformance_Parses()
    {
        var lines = new List<string>
        {
            "Power Scheme GUID: 49371465-2b86-4782-9e84-816d3f61e3c8  (Ultimate Performance)"
        };

        var (name, guid) = PerformanceService.ParseActivePlan(lines);

        Assert.Equal("Ultimate Performance", name);
        Assert.Equal("49371465-2b86-4782-9e84-816d3f61e3c8", guid);
    }

    [Fact]
    public void ParseActivePlan_EmptyInput_ReturnsUnknown()
    {
        var (name, guid) = PerformanceService.ParseActivePlan(new List<string>());

        Assert.Equal("Unknown", name);
        Assert.Equal("", guid);
    }

    [Fact]
    public void ParseActivePlan_NoGuidLine_ReturnsUnknown()
    {
        var lines = new List<string> { "some random text", "another line" };

        var (name, guid) = PerformanceService.ParseActivePlan(lines);

        Assert.Equal("Unknown", name);
        Assert.Equal("", guid);
    }

    [Fact]
    public void ParseActivePlan_HighPerformance_Parses()
    {
        var lines = new List<string>
        {
            "Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)"
        };

        var (name, guid) = PerformanceService.ParseActivePlan(lines);

        Assert.Equal("High performance", name);
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", guid);
    }

    [Fact]
    public void ParseActivePlan_NonEnglishLabel_StillParsesGuid()
    {
        // Regression for the locale bug: the old parser keyed off the English "GUID:"
        // substring, which is translated on non-English Windows, so it returned ("Unknown", "")
        // and Restore then skipped the power-plan restore entirely. The parser now anchors on
        // the canonical GUID token, which powercfg prints identically in every language.
        // German example ("Energieschema-GUID:"):
        var lines = new List<string>
        {
            "Energieschema-GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Ausbalanciert)"
        };

        var (name, guid) = PerformanceService.ParseActivePlan(lines);

        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", guid);
        Assert.Equal("Ausbalanciert", name);
    }

    // ── ParsePlanGuidByName ──

    [Fact]
    public void ParsePlanGuidByName_FindsMatchingPlan()
    {
        var lines = new List<string>
        {
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)",
            "Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)",
            "Power Scheme GUID: 49371465-2b86-4782-9e84-816d3f61e3c8  (Ultimate Performance) *",
        };

        var guid = PerformanceService.ParsePlanGuidByName(lines, "Ultimate Performance");

        Assert.Equal("49371465-2b86-4782-9e84-816d3f61e3c8", guid);
    }

    [Fact]
    public void ParsePlanGuidByName_NoMatch_ReturnsNull()
    {
        var lines = new List<string>
        {
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)",
        };

        var guid = PerformanceService.ParsePlanGuidByName(lines, "Ultimate Performance");

        Assert.Null(guid);
    }

    [Fact]
    public void ParsePlanGuidByName_EmptyInput_ReturnsNull()
    {
        var guid = PerformanceService.ParsePlanGuidByName(new List<string>(), "Balanced");
        Assert.Null(guid);
    }

    // ── EnsureUltimatePerformancePlanAsync on translated Windows (#2438) ──
    //
    // powercfg translates both the "Power Scheme GUID:" label and the plan names; only the GUID is the same in
    // every language, which is why ParseActivePlan already anchors on it. The labels below are illustrative
    // translations chosen to NOT contain the English "GUID:" — the property under test is that nothing
    // depends on the label, not the exact wording of any one language.

    private const string NewPlanGuid = "3ff9831b-6f80-4830-8178-736cd4229e7b";

    /// <summary>A runner that answers each powercfg call with the lines a translated Windows would print.</summary>
    private static IPowerShellRunner PowercfgAnswering(string[] listLines, string[] duplicateLines)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        void Answer(string arguments, string[] lines) =>
            runner.RunProcessAsync("powercfg.exe", arguments, Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
                  .Returns(_ =>
                  {
                      foreach (var line in lines)
                          runner.LineReceived += Raise.Event<Action<SysManager.Models.PowerShellLine>>(
                              SysManager.Models.PowerShellLine.Output(line));
                      return 0;
                  });
        Answer("/list", listLines);
        Answer($"-duplicatescheme {PerformanceService.UltimatePerfScheme}", duplicateLines);
        return runner;
    }

    [Fact]
    public async Task EnsureUltimatePerformancePlan_OnTranslatedWindows_ReturnsTheNewPlansGuid()
    {
        var runner = PowercfgAnswering(
            ["Identificador del esquema: 381b4222-f694-41f0-9685-ff5bb260df2e  (Equilibrado) *"],
            [$"Identificador del esquema: {NewPlanGuid}  (Máximo rendimiento)"]);

        var guid = await NewService(runner).EnsureUltimatePerformancePlanAsync();

        Assert.Equal(NewPlanGuid, guid);
    }

    [Fact]
    public async Task EnsureUltimatePerformancePlan_WhenWindowsListsTheBuiltInScheme_UsesItWithoutCopying()
    {
        // The built-in scheme's GUID is the same in every language, so when /list shows it there is nothing to
        // duplicate — and a translated name can no longer make each click add another copy of the plan.
        var runner = PowercfgAnswering(
            [$"Identificador del esquema: {PerformanceService.UltimatePerfScheme}  (Máximo rendimiento)"],
            [$"Identificador del esquema: {NewPlanGuid}  (Máximo rendimiento)"]);

        var guid = await NewService(runner).EnsureUltimatePerformancePlanAsync();

        Assert.Equal(PerformanceService.UltimatePerfScheme, guid);
        await runner.DidNotReceive().RunProcessAsync(
            "powercfg.exe", Arg.Is<string>(a => a.StartsWith("-duplicatescheme", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task EnsureUltimatePerformancePlan_WhenWindowsYieldsNoPlan_ReturnsEmpty()
    {
        // The honest "no" the command now reports: powercfg printed no GUID at all.
        var runner = PowercfgAnswering([], ["Unable to perform operation. An unexpected error (0x65b) has occurred."]);

        Assert.Equal("", await NewService(runner).EnsureUltimatePerformancePlanAsync());
    }

    [Fact]
    public void ParsePlanGuidByName_CaseInsensitive()
    {
        var lines = new List<string>
        {
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (balanced)",
        };

        var guid = PerformanceService.ParsePlanGuidByName(lines, "Balanced");

        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", guid);
    }

    // ── ParseProcessorMinPercent ──

    [Fact]
    public void ParseProcessorMinPercent_100Percent_Returns100()
    {
        var lines = new List<string>
        {
            "Power Setting GUID: 893dee8e-2bef-41e0-89c6-b55d0929964c  (Processor performance core parking min cores)",
            "  Minimum Possible Setting: 0x00000000",
            "  Maximum Possible Setting: 0x00000064",
            "  Current AC Power Setting Index: 0x00000064",
            "  Current DC Power Setting Index: 0x00000064",
        };

        var result = PerformanceService.ParseProcessorMinPercent(lines);

        Assert.Equal(100, result);
    }

    [Fact]
    public void ParseProcessorMinPercent_5Percent_Returns5()
    {
        var lines = new List<string>
        {
            "  Current AC Power Setting Index: 0x00000005",
            "  Current DC Power Setting Index: 0x00000005",
        };

        var result = PerformanceService.ParseProcessorMinPercent(lines);

        Assert.Equal(5, result);
    }

    [Fact]
    public void ParseProcessorMinPercent_EmptyInput_ReturnsNull()
    {
        // Behavior change (bug fix): the parser used to return a fabricated 5 when it
        // couldn't read the value. That caused Restore to WRITE 5% back on machines where
        // the read failed (non-English powercfg output). It now returns null = "unknown",
        // and the snapshot/restore path treats null as "leave the setting untouched".
        var result = PerformanceService.ParseProcessorMinPercent(new List<string>());
        Assert.Null(result);
    }

    [Fact]
    public void ParseProcessorMinPercent_NoMatchingLine_ReturnsNull()
    {
        var lines = new List<string> { "some random text" };
        var result = PerformanceService.ParseProcessorMinPercent(lines);
        Assert.Null(result);
    }

    [Fact]
    public void ParseProcessorMinPercent_NonEnglishLabel_RealPowercfgShape_ReturnsAcValue()
    {
        // Regression for the locale bug (completes an earlier, incomplete fix). REAL powercfg
        // output for PROCTHROTTLEMIN prints Minimum/Maximum/increment hex values BEFORE the two
        // current-index lines, and on non-English Windows the "Current AC/DC" labels are
        // translated, so the English-label fast path misses. The previous fallback then returned
        // the FIRST hex token — "Minimum Possible Setting" = 0x0 — so it read 0 (not the AC
        // value), and a later Restore wrote 0% back. The AC index is always the second-to-last
        // hex token (AC-before-DC, current-index lines last, in every language). German example
        // (the labels are illustrative; the value ordering is what powercfg guarantees):
        var lines = new List<string>
        {
            "  Mögliche Mindesteinstellung: 0x00000000",
            "  Mögliche Höchsteinstellung: 0x00000064",
            "  Inkrement für mögliche Einstellungen: 0x00000001",
            "  Index für aktuelle Wechselstromeinstellung: 0x00000032",
            "  Index für aktuelle Gleichstromeinstellung: 0x00000019",
        };

        var result = PerformanceService.ParseProcessorMinPercent(lines);

        Assert.Equal(0x32, result); // 50 — AC (2nd-to-last), NOT Minimum-Possible 0x0 or DC 0x19
    }

    [Fact]
    public void ParseProcessorMinPercent_NonEnglishLabel_TwoTokensOnly_ReturnsAcNotDc()
    {
        // Minimal non-English shape (no Min/Max/increment lines): the AC index is still the
        // second-to-last hex token, so the parser must return AC (50), never the trailing DC (25).
        var lines = new List<string>
        {
            "  Index für aktuelle Wechselstromeinstellung: 0x00000032",
            "  Index für aktuelle Gleichstromeinstellung: 0x00000019",
        };

        var result = PerformanceService.ParseProcessorMinPercent(lines);

        Assert.Equal(0x32, result); // 50 (AC), not 0x19 = 25 (DC)
    }

    [Fact]
    public void ParseProcessorMinPercent_50Percent_Returns50()
    {
        var lines = new List<string>
        {
            "  Current AC Power Setting Index: 0x00000032",
        };

        var result = PerformanceService.ParseProcessorMinPercent(lines);

        Assert.Equal(50, result);
    }

    // ── Constants ──

    [Fact]
    public void BalancedGuid_IsCorrect()
    {
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", PerformanceService.BalancedGuid);
    }

    [Fact]
    public void HighPerfGuid_IsCorrect()
    {
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", PerformanceService.HighPerfGuid);
    }

    [Fact]
    public void UltimatePerfScheme_IsCorrect()
    {
        Assert.Equal("e9a42b02-d5df-448d-aa00-03f14749eb61", PerformanceService.UltimatePerfScheme);
    }

    // ── Snapshot trust boundary ──

    [Theory]
    [InlineData("not-a-guid", "Balanced", 5, null)]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", "", 5, null)]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced\nSpoofed", 5, null)]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced\u202ESpoofed", 5, null)]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced", -1, null)]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced", 101, null)]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced", 5, @"..\0000")]
    public void SnapshotValidation_RejectsUnsafeFields(
        string guid,
        string name,
        int processorMinimum,
        string? nvidiaSubKey)
    {
        var snapshot = new PerformanceService.OriginalSnapshot(
            guid,
            name,
            true,
            true,
            true,
            true,
            true,
            processorMinimum,
            nvidiaSubKey);

        Assert.False(PerformanceService.TryValidateSnapshot(snapshot, out _));
    }

    [Fact]
    public void SnapshotValidation_AcceptsLegacyUnknownValues()
    {
        var snapshot = new PerformanceService.OriginalSnapshot(
            PowerPlanGuid: "",
            PowerPlanName: "Unknown",
            UiEffectsEnabled: true,
            GameModeEnabled: true,
            XboxGameBarEnabled: true,
            XboxGameDvrEnabled: true,
            GpuDynamicPstate: true,
            ProcessorMinPercentAc: null,
            NvidiaSubKey: null);

        Assert.True(PerformanceService.TryValidateSnapshot(snapshot, out _));
    }

    [Fact]
    public async Task SetActivePlan_NonzeroExitCode_Throws()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(5);
        using var service = NewService(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetActivePlanAsync(PerformanceService.BalancedGuid));
    }

    [Fact]
    public async Task SetProcessorMinimum_NonzeroExitCode_ThrowsBeforeReportingSuccess()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(0);
        runner.RunProcessAsync(
                "powercfg.exe",
                Arg.Is<string>(args =>
                    args != null
                    && args.StartsWith("/setdcvalueindex", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(7);
        using var service = NewService(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetProcessorMinStateAsync(5));
    }

    // ── OriginalSnapshot ──

    [Fact]
    public void OriginalSnapshot_RecordEquality()
    {
        var a = new PerformanceService.OriginalSnapshot(
            "guid1", "Balanced", true, true, true, true, true, 5, null);
        var b = new PerformanceService.OriginalSnapshot(
            "guid1", "Balanced", true, true, true, true, true, 5, null);

        Assert.Equal(a, b);
    }

    [Fact]
    public void OriginalSnapshot_DifferentValues_NotEqual()
    {
        var a = new PerformanceService.OriginalSnapshot(
            "guid1", "Balanced", true, true, true, true, true, 5, null);
        var b = new PerformanceService.OriginalSnapshot(
            "guid2", "High", false, false, false, false, false, 100, "0000");

        Assert.NotEqual(a, b);
    }
}
