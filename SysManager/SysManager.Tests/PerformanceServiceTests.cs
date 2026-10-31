// SysManager · PerformanceServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PerformanceService"/>. Focuses on parsing logic
/// since powercfg/registry calls are integration-level.
/// </summary>
public class PerformanceServiceTests : IDisposable
{
    // The folder each test's service keeps its files in. The Ultimate step records the copy it makes, and over the
    // real profile a test would write its made-up copy into the records of whoever runs the suite.
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "SysManagerPerformanceTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    // Through the test constructor, which writes nothing to the PC unless given a write: the public one passes the
    // real ones.
    private PerformanceService NewService(IPowerShellRunner runner) =>
        new(runner, new RestorePointService(runner), _dir, processes: null);

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

    // ── The Ultimate copy it made, used again in any language (#2717) ──
    //
    // Home and Pro hide the stock scheme, so Ultimate Performance there is a copy of it, and Windows names the copy in
    // the display language. Looked for only by its English name, the copy was never found again outside English, and
    // every Ultimate step made another.

    private const string BalancedListed = "Identificador del esquema: 381b4222-f694-41f0-9685-ff5bb260df2e  (Equilibrado) *";
    private const string SecondCopyGuid = "5c1f9e2a-7b3d-4e8f-9a6c-1d2e3f4a5b6c";

    /// <summary>
    /// powercfg on a Spanish Windows that does not list the stock scheme: /list shows <paramref name="listed"/>, and
    /// each -duplicatescheme makes the next of <paramref name="copies"/>, which /list shows from then on.
    /// </summary>
    private static IPowerShellRunner TranslatedWindows(List<string> listed, params string[] copies)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var next = new Queue<string>(copies);
        void Say(string line) =>
            runner.LineReceived += Raise.Event<Action<SysManager.Models.PowerShellLine>>(
                SysManager.Models.PowerShellLine.Output(line));
        runner.RunProcessAsync("powercfg.exe", "/list", Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(_ =>
              {
                  foreach (var line in listed.ToArray()) Say(line);
                  return 0;
              });
        runner.RunProcessAsync("powercfg.exe", $"-duplicatescheme {PerformanceService.UltimatePerfScheme}",
                               Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(_ =>
              {
                  var made = $"Identificador del esquema: {next.Dequeue()}  (Máximo rendimiento)";
                  listed.Add(made);
                  Say(made);
                  return 0;
              });
        return runner;
    }

    /// <summary>powercfg reports <paramref name="line"/> as the active plan.</summary>
    private static void ActivePlanIs(IPowerShellRunner runner, string line) =>
        runner.RunProcessAsync("powercfg.exe", "/getactivescheme", Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(_ =>
              {
                  runner.LineReceived += Raise.Event<Action<SysManager.Models.PowerShellLine>>(
                      SysManager.Models.PowerShellLine.Output(line));
                  return 0;
              });

    private static Task<int> CopiesMade(IPowerShellRunner runner, int count) =>
        runner.Received(count).RunProcessAsync(
            "powercfg.exe", Arg.Is<string>(a => a.StartsWith("-duplicatescheme", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());

    [Fact]
    public async Task EnsureUltimatePerformancePlan_OnTranslatedWindows_UsesTheCopyItMadeAgain_AfterARestartToo()
    {
        var runner = TranslatedWindows([BalancedListed], NewPlanGuid, SecondCopyGuid);

        using (var service = NewService(runner))
        {
            Assert.Equal(NewPlanGuid, await service.EnsureUltimatePerformancePlanAsync());
            Assert.Equal(NewPlanGuid, await service.EnsureUltimatePerformancePlanAsync());
        }
        // A new service, as when SysManager is started again, finds it through the record the first one kept.
        using (var restarted = NewService(runner))
            Assert.Equal(NewPlanGuid, await restarted.EnsureUltimatePerformancePlanAsync());

        await CopiesMade(runner, 1);
    }

    [Fact]
    public async Task EnsureUltimatePerformancePlan_WhenTheCopyItMadeIsGone_MakesAnother_AndUsesThatOneAgain()
    {
        List<string> listed = [BalancedListed];
        var runner = TranslatedWindows(listed, NewPlanGuid, SecondCopyGuid);
        using var service = NewService(runner);
        Assert.Equal(NewPlanGuid, await service.EnsureUltimatePerformancePlanAsync());

        // Deleted in Power Options: switching to a plan that is no longer there would fail.
        listed.RemoveAll(line => line.Contains(NewPlanGuid, StringComparison.Ordinal));

        Assert.Equal(SecondCopyGuid, await service.EnsureUltimatePerformancePlanAsync());
        Assert.Equal(SecondCopyGuid, await service.EnsureUltimatePerformancePlanAsync());
        await CopiesMade(runner, 2);
    }

    [Theory]
    [InlineData("Máximo rendimiento")]                                             // not JSON
    [InlineData("{}")]                                                             // no GUID
    [InlineData("{\"PlanGuid\":null}")]
    [InlineData("{\"PlanGuid\":\"3ff9831b-6f80-4830-8178-736cd4229e7b /delete\"}")]    // more than a GUID
    [InlineData("{\"PlanGuid\":\"3ff9831b-6f80-4830-8178-736cd4229e7b\\n\"}")]     // white space around it (#2741)
    [InlineData("{\"PlanGuid\":\"3ff9831b-6f80-4830-8178-736cd4229e7b\",\"PlanGuid\":\"5c1f9e2a-7b3d-4e8f-9a6c-1d2e3f4a5b6c\"}")]
    public async Task EnsureUltimatePerformancePlan_WithARecordThatCannotBeUsed_MakesACopy_AndKeepsThatOne(string record)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, PerformanceService.UltimateCopyFileName), record);
        var runner = TranslatedWindows([BalancedListed], NewPlanGuid, SecondCopyGuid);
        using var service = NewService(runner);
        Assert.Null(service.ReadUltimateCopy());

        Assert.Equal(NewPlanGuid, await service.EnsureUltimatePerformancePlanAsync());
        Assert.Equal(NewPlanGuid, await service.EnsureUltimatePerformancePlanAsync());
        await CopiesMade(runner, 1);
    }

    [Fact]
    public void TheUltimateCopysRecord_IsNotReadPastTheSizeOfARecord()
    {
        // The app often runs as administrator and the record is in the user's own folder, so a file grown large is
        // not read whole into memory.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, PerformanceService.UltimateCopyFileName),
            $"{{\"PlanGuid\":\"{NewPlanGuid}\"}}" + new string(' ', PerformanceService.MaxSnapshotBytes));

        Assert.Null(NewService(Substitute.For<IPowerShellRunner>()).ReadUltimateCopy());
    }

    [Theory]
    [InlineData(NewPlanGuid, "Máximo rendimiento", true, "Ultimate Performance")]
    [InlineData("0d2e9b4f-1a0c-4c6b-8d7e-2f3a4b5c6d7e", "Mi plan", false, "Mi plan")]
    public async Task ReadProfile_KnowsTheCopyItMade_UnderItsTranslatedName(
        string activeGuid, string activeName, bool isTheCopy, string summary)
    {
        var runner = TranslatedWindows([BalancedListed], NewPlanGuid);
        using var service = NewService(runner);
        Assert.Equal(NewPlanGuid, await service.EnsureUltimatePerformancePlanAsync());
        ActivePlanIs(runner, $"Identificador del esquema: {activeGuid}  ({activeName})");

        var profile = await service.ReadProfileAsync();

        Assert.Equal(isTheCopy, profile.IsUltimateCopy);
        Assert.Equal(summary, profile.ProfileSummary);
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

    // ── the battery minimum beside it (#2716) ──

    [Fact]
    public void ParseProcessorMinPercents_InEnglish_ReadEachByItsLabel()
    {
        var lines = new List<string>
        {
            "  Minimum Possible Setting: 0x00000000",
            "  Maximum Possible Setting: 0x00000064",
            "  Current AC Power Setting Index: 0x00000005",
            "  Current DC Power Setting Index: 0x0000000a",
        };

        Assert.Equal((5, 10), PerformanceService.ParseProcessorMinPercents(lines));
    }

    [Fact]
    public void ParseProcessorMinPercents_InAnotherLanguage_ReadTheLastTwoValues_AcFirst()
    {
        var lines = new List<string>
        {
            "  Mögliche Mindesteinstellung: 0x00000000",
            "  Mögliche Höchsteinstellung: 0x00000064",
            "  Inkrement für mögliche Einstellungen: 0x00000001",
            "  Index für aktuelle Wechselstromeinstellung: 0x00000032",
            "  Index für aktuelle Gleichstromeinstellung: 0x00000019",
        };

        Assert.Equal((0x32, 0x19), PerformanceService.ParseProcessorMinPercents(lines));
    }

    [Fact]
    public void ParseProcessorMinPercents_WithOneValue_ReadNeither()
    {
        Assert.Equal((null, null), PerformanceService.ParseProcessorMinPercents(["  Index: 0x00000032"]));
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
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e\n", "Balanced", 5, null)]   // white space around it (#2741)
    [InlineData(" 381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced", 5, null)]
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

    // ── The plan powercfg is handed (#2741) ──
    // Game mode's crash recovery reads this GUID back from a file in the user's profile, and the check here is the only
    // one on it before it reaches powercfg's command line.

    [Theory]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e & calc")]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e\n")]
    [InlineData("{381b4222-f694-41f0-9685-ff5bb260df2e}")]
    [InlineData("381b4222f69441f09685ff5bb260df2e")]
    [InlineData("SCHEME_BALANCED")]
    [InlineData("")]
    [InlineData(" 381b4222-f694-41f0-9685-ff5bb260df2e")]
    public async Task SetActivePlan_RefusesAnythingButACanonicalGuid_BeforeRunningPowercfg(string guid)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        using var service = NewService(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetActivePlanAsync(guid));

        Assert.Empty(runner.ReceivedCalls());
    }

    [Fact]
    public async Task SetActivePlan_WithACanonicalGuid_HandsItToPowercfg()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(0);
        using var service = NewService(runner);

        await service.SetActivePlanAsync(PerformanceService.BalancedGuid.ToUpperInvariant());

        // As powercfg prints a plan, whatever case it was recorded in.
        await runner.Received(1).RunProcessAsync("powercfg.exe", $"/setactive {PerformanceService.BalancedGuid}",
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
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

    // ── The graphics step of a restore ──

    private static PerformanceService.OriginalSnapshot WithCard(string? subKey, bool dynamicPstate = true) =>
        new("381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced", true, true, true, true, dynamicPstate, 5, subKey);

    // The card's setting as it is now: true for maximum performance, null when it could not be read.
    private static PerformanceService.GraphicsStep StepFor(
        PerformanceService.OriginalSnapshot snapshot, PerformanceService.RecordedAdapter adapter, bool? maxPerformanceNow) =>
        PerformanceService.GraphicsStepOf(snapshot, _ => adapter, _ => maxPerformanceNow);

    [Fact]
    public void RestoringGraphics_ForACardThatIsGone_LeavesItOut_SoTheRestCanFinish()
    {
        // Taken out, or its driver removed: there is nothing to write the setting to, and stopping here left the
        // processor minimum and the record behind for good (#1525).
        var writes = new List<(string, bool)>();
        var step = StepFor(WithCard("0000"), PerformanceService.RecordedAdapter.Gone, maxPerformanceNow: null);

        PerformanceService.RestoreGraphics(WithCard("0000"), step, (key, max) => { writes.Add((key, max)); return true; });

        Assert.Equal(PerformanceService.GraphicsStep.LeftOut, step);
        Assert.Empty(writes);
    }

    [Theory]
    [InlineData(true)]    // set to maximum performance since, so it differs from the recorded adaptive setting
    [InlineData(null)]    // could not be read: written, as it may differ
    public void RestoringGraphics_ForACardThatIsThere_WritesItsOwnSettingBack(bool? maxPerformanceNow)
    {
        var writes = new List<(string, bool)>();
        var snapshot = WithCard("0001", dynamicPstate: true);
        var step = StepFor(snapshot, PerformanceService.RecordedAdapter.Present, maxPerformanceNow);

        PerformanceService.RestoreGraphics(snapshot, step, (key, max) => { writes.Add((key, max)); return true; });

        Assert.Equal(PerformanceService.GraphicsStep.Write, step);
        Assert.Equal([("0001", false)], writes);
    }

    [Theory]
    [InlineData(true, false)]   // recorded adaptive, adaptive now
    [InlineData(false, true)]   // recorded maximum performance, maximum performance now
    public void RestoringGraphics_ForACardSetAsRecorded_WritesNothing(bool dynamicPstate, bool maxPerformanceNow)
    {
        // The write needs administrator rights and a restart, and was made on every restore whatever the card's value,
        // so a standard user's Restore All stopped at it and an administrator's always asked for a restart (#2716).
        var writes = new List<(string, bool)>();
        var snapshot = WithCard("0001", dynamicPstate);
        var step = StepFor(snapshot, PerformanceService.RecordedAdapter.Present, maxPerformanceNow);

        PerformanceService.RestoreGraphics(snapshot, step, (key, max) => { writes.Add((key, max)); return true; });

        Assert.Equal(PerformanceService.GraphicsStep.AlreadySet, step);
        Assert.Empty(writes);
    }

    [Theory]
    [InlineData("Present")]
    [InlineData("Unreadable")]
    public void RestoringGraphics_ThatCouldNotBeWritten_Stops_WhenTheCardMayStillBeThere(string found)
    {
        // A card whose key could not be read may still be there, so leaving its setting out would lose it for good.
        var adapter = Enum.Parse<PerformanceService.RecordedAdapter>(found);
        var step = StepFor(WithCard("0000"), adapter, maxPerformanceNow: true);

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            PerformanceService.RestoreGraphics(WithCard("0000"), step, (_, _) => false));

        Assert.Equal("The NVIDIA graphics setting could not be restored.", thrown.Message);
    }

    [Fact]
    public void RestoringGraphics_WithNoCardRecorded_AsksNothing()
    {
        var asked = 0;

        var step = PerformanceService.GraphicsStepOf(WithCard(null),
            _ => { asked++; return PerformanceService.RecordedAdapter.Present; },
            _ => { asked++; return true; });
        PerformanceService.RestoreGraphics(WithCard(null), step, (_, _) => { asked++; return true; });

        Assert.Equal(PerformanceService.GraphicsStep.None, step);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void ARecordedCardThatIsNotAKey_IsGone_WithoutTheRegistryBeingRead()
    {
        Assert.Equal(PerformanceService.RecordedAdapter.Gone, PerformanceService.FindRecordedAdapter(@"..\Other"));
        Assert.False(PerformanceService.IsNvidiaSubKey(@"..\Other"));
    }

    [Fact]
    public void ARecordedCard_IsThere_OnlyWhileItsKeyStillNamesNvidia()
    {
        // The driver's name or its maker's will do; a key that now belongs to another card, or is not there, is gone.
        Assert.Equal(PerformanceService.RecordedAdapter.Present,
            PerformanceService.FindRecordedAdapter("0000", _ => ("NVIDIA GeForce RTX 4070", "")));
        Assert.Equal(PerformanceService.RecordedAdapter.Present,
            PerformanceService.FindRecordedAdapter("0000", _ => ("", "NVIDIA")));
        Assert.Equal(PerformanceService.RecordedAdapter.Gone,
            PerformanceService.FindRecordedAdapter("0000", _ => ("AMD Radeon RX 7800 XT", "Advanced Micro Devices, Inc.")));
        Assert.Equal(PerformanceService.RecordedAdapter.Gone, PerformanceService.FindRecordedAdapter("0000", _ => null));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("security")]
    [InlineData("access")]
    public void ARecordedCardWhoseKeyCannotBeRead_IsNotKnown_RatherThanGone(string failure)
    {
        // "Gone" leaves its setting out of the restore and lets the record be deleted, while the card may still be there.
        Exception thrown = failure switch
        {
            "io" => new System.IO.IOException("The registry key is marked for deletion."),
            "security" => new System.Security.SecurityException("Requested registry access is not allowed."),
            _ => new UnauthorizedAccessException("Access to the registry key is denied."),
        };

        Assert.Equal(PerformanceService.RecordedAdapter.Unreadable,
            PerformanceService.FindRecordedAdapter("0000", _ => throw thrown));
    }

    // ── The NVIDIA key of a card that is in the PC (#2836) ──
    // Windows keeps a removed card's key until the device is uninstalled, numbered before its replacement's.

    private static (string DriverDesc, string Provider)? AdapterNames(string subKey) => subKey switch
    {
        "0000" => ("NVIDIA GeForce GTX 1060", "NVIDIA"),                         // the card taken out
        "0001" => ("NVIDIA GeForce RTX 4070", "NVIDIA"),                         // the card in the PC
        "0002" => ("AMD Radeon RX 7800 XT", "Advanced Micro Devices, Inc."),
        _ => null,
    };

    [Fact]
    public void TheNvidiaKey_IsTheCardInThePC_NotARemovedOneBeforeIt()
        => Assert.Equal("0001", PerformanceService.FindNvidiaSubKey(
            ["0000", "0001", "0002"], AdapterNames, new HashSet<string> { "0001" }));

    [Fact]
    public void AfterAMoveToAnotherMaker_NoNvidiaKeyIsFound()
        => Assert.Null(PerformanceService.FindNvidiaSubKey(
            ["0000", "0002"], AdapterNames, new HashSet<string> { "0002" }));

    [Fact]
    public void WhenWhichAdaptersAreInThePCIsNotKnown_TheFirstNvidiaKeyIsTaken_AsBefore()
        => Assert.Equal("0000", PerformanceService.FindNvidiaSubKey(["0000", "0001"], AdapterNames, present: null));

    [Fact]
    public void ARecordedCardThatIsNoLongerInThePC_IsGone()
    {
        // Its key still names NVIDIA, so a restore wrote the setting back where no driver reads it.
        Assert.Equal(PerformanceService.RecordedAdapter.Gone,
            PerformanceService.FindRecordedAdapter("0000", AdapterNames, () => new HashSet<string> { "0001" }));
        Assert.Equal(PerformanceService.RecordedAdapter.Present,
            PerformanceService.FindRecordedAdapter("0001", AdapterNames, () => new HashSet<string> { "0001" }));
        Assert.Equal(PerformanceService.RecordedAdapter.Present,
            PerformanceService.FindRecordedAdapter("0000", AdapterNames, () => null));
    }

    [Fact]
    public void WhichAdaptersAreInThePC_IsNotAsked_ForAKeyThatCannotBeOne()
    {
        var asked = 0;

        Assert.Equal(PerformanceService.RecordedAdapter.Gone,
            PerformanceService.FindRecordedAdapter(@"..\Other", AdapterNames, () => { asked++; return null; }));
        Assert.Equal(0, asked);
    }

    [Theory]
    [InlineData(@"{4d36e968-e325-11ce-bfc1-08002be10318}\0001", "0001")]
    [InlineData(@"{4D36E968-E325-11CE-BFC1-08002BE10318}\0003", "0003")]
    [InlineData(@"{4d36e972-e325-11ce-bfc1-08002be10318}\0001", null)]       // a network adapter's class
    [InlineData(@"{4d36e968-e325-11ce-bfc1-08002be10318}\..\0001", null)]
    [InlineData(@"{4d36e968-e325-11ce-bfc1-08002be10318}", null)]
    [InlineData(null, null)]
    public void ClassSubKeyOf_ReadsADisplayDriversSubkey_AndNothingElse(string? driver, string? expected)
        => Assert.Equal(expected, PerformanceService.ClassSubKeyOf(driver));

    [Fact]
    public void TheAdaptersInThePC_AreTheSubkeysTheirDriversName()
    {
        var drivers = new Dictionary<string, string?>
        {
            [@"PCI\VEN_10DE&DEV_2786\4&1A2B3C4D&0&0008"] = @"{4d36e968-e325-11ce-bfc1-08002be10318}\0001",
            [@"PCI\VEN_8086&DEV_A780\3&11583659&0&10"] = @"{4d36e968-e325-11ce-bfc1-08002be10318}\0002",
            [@"ROOT\DISPLAY\0000"] = null,
        };

        var present = PerformanceService.PresentAdapterKeys(drivers.Keys, device => drivers[device]);

        Assert.Equal(["0001", "0002"], present!.Order());
    }

    [Fact]
    public void NoAdapterInThePC_IsAReadThatDidNotWork_NotAPCWithoutOne()
    {
        // Every PC has a display adapter. Taken as an answer, an empty one would hide the NVIDIA card it has.
        Assert.Null(PerformanceService.PresentAdapterKeys([], _ => null));
        Assert.Null(PerformanceService.PresentAdapterKeys([@"ROOT\DISPLAY\0000"], _ => null));
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

    // ── Game Mode and visual effects as Windows keeps them (#2716) ──

    /// <summary>
    /// Game Mode is read from the value the Settings switch writes first. Only the Windows 10 1703 value was read, so
    /// with Game Mode off in Settings and that value absent the tab showed it on and recorded "on" to put back.
    /// </summary>
    [Theory]
    [InlineData(0, null, false)] // off in Settings, the old value absent: the case that read as on
    [InlineData(1, null, true)]
    [InlineData(0, 1, false)]    // the Settings value wins over a stale old one
    [InlineData(1, 0, true)]
    [InlineData(null, 0, false)] // only the old value
    [InlineData(null, 1, true)]
    [InlineData(null, null, true)] // neither: Windows' default, on
    public void GameMode_IsReadFromTheSettingsSwitchsValueFirst(int? autoGameModeEnabled, int? allowAutoGameMode, bool on) =>
        Assert.Equal(on, PerformanceService.GameModeOn(autoGameModeEnabled, allowAutoGameMode));

    [Fact]
    public void GameMode_ThatIsNotADword_ReadsOff()
    {
        Assert.False(PerformanceService.GameModeOn("1", null));
    }

    /// <summary>
    /// The visual-effects write goes into the user's profile as well as the session. With SPIF_SENDCHANGE alone the
    /// change, and a restore of it, lasted until the next sign-out.
    /// </summary>
    // ── The recorded power plan, among the plans on the PC (#2716) ──

    [Fact]
    public void ARecordedPlan_IsThere_WhenWindowsListsIt_InAnyLetterCase()
    {
        var plans = new HashSet<Guid> { Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e") };

        Assert.Equal(PerformanceService.RecordedPlan.Present,
            PerformanceService.FindRecordedPlan("381B4222-F694-41F0-9685-FF5BB260DF2E", plans));
    }

    [Fact]
    public void ARecordedPlan_IsGone_WhenWindowsListsItsPlansWithoutIt()
    {
        var plans = new HashSet<Guid> { Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c") };

        Assert.Equal(PerformanceService.RecordedPlan.Gone,
            PerformanceService.FindRecordedPlan("381b4222-f694-41f0-9685-ff5bb260df2e", plans));
    }

    [Fact]
    public void ARecordedPlan_IsNotKnown_WhenThePlansCouldNotBeListed()
    {
        // Not known is not gone: gone leaves the plan out of the restore and lets the record be deleted.
        Assert.Equal(PerformanceService.RecordedPlan.Unreadable,
            PerformanceService.FindRecordedPlan("381b4222-f694-41f0-9685-ff5bb260df2e", plans: null));
    }

    [Fact]
    public void TheVisualEffectsWrite_KeepsTheChangeInTheProfile_AndAnnouncesIt()
    {
        const uint updateIniFile = 0x01, sendChange = 0x02;
        Assert.Equal(updateIniFile | sendChange, PerformanceService.UiEffectsWrite);
    }
}
