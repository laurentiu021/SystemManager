// SysManager · PrivacyServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PrivacyService"/>'s apply-result signalling (regression for
/// the "false success on a swallowed write failure" defect). Uses toggles whose
/// registry path has an unrecognized hive so <c>ApplyToggle</c> is refused WITHOUT
/// touching the real registry — deterministic and side-effect-free.
/// </summary>
public sealed class PrivacyServiceTests : IDisposable
{
    /// <summary>
    /// This test's own ledger folder, so what one test records about a value is never what another puts back: the
    /// ledger is keyed by the value's path, and the tests below write the same paths under roots of their own.
    /// </summary>
    private readonly string _ledgerDir =
        Path.Combine(Path.GetTempPath(), "SysManagerPrivacyLedgerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_ledgerDir, recursive: true); }
        catch (DirectoryNotFoundException) { /* nothing was recorded */ }
    }

    private PrivacyLedgerService Ledger() => new(_ledgerDir);

    private static PrivacyToggle BadHiveToggle(string name = "bad") => new()
    {
        Key = name,
        Name = name,
        Description = "unrecognized hive — write cannot succeed",
        Category = "Telemetry",
        RegistryPath = @"NOPE\Software\SysManagerTests\Privacy",
        ValueName = "Value",
        EnabledValue = 0,
        DisabledValue = 1
    };

    [Fact]
    public void ApplyToggle_UnwritableHive_IsRefused()
    {
        var svc = new PrivacyService(ledger: Ledger());
        Assert.Equal(PrivacyWriteOutcome.Refused, svc.ApplyToggle(BadHiveToggle()));
    }

    [Fact]
    public void ApplyAll_ReturnsTheTogglesThatFailed_WithWhy()
    {
        var svc = new PrivacyService(ledger: Ledger());
        var a = BadHiveToggle("a");
        var b = BadHiveToggle("b");

        var failed = svc.ApplyAll([a, b]);

        // Both writes fail (unrecognized hive) so both come back in the failed list —
        // the caller must not treat them as applied.
        Assert.Equal([new PrivacyWriteFailure(a, PrivacyWriteOutcome.Refused),
            new PrivacyWriteFailure(b, PrivacyWriteOutcome.Refused)], failed);
    }

    [Fact]
    public void ApplyToggle_NullToggle_Throws()
    {
        var svc = new PrivacyService(ledger: Ledger());
        Assert.Throws<ArgumentNullException>(() => svc.ApplyToggle(null!));
    }

    /// <summary>
    /// Every toggle keeps the key a privacy profile names it by (#1530).
    /// </summary>
    /// <remarks>
    /// A key is a promise to every profile already exported. Renamed, the profile's choice for that toggle is
    /// dropped as unknown on import, and the user is told one choice could not be read instead of seeing the
    /// switch move. So the list is pinned exactly: changing a key has to be a decision made here, and adding a
    /// toggle means adding its key.
    /// </remarks>
    [Fact]
    public void EveryToggle_KeepsTheKeyProfilesNameItBy()
    {
        var keys = new PrivacyService(ledger: Ledger()).LoadToggles().Select(t => t.Key);

        Assert.Equal(
            ["diagnostic-data", "activity-history", "advertising-id", "feedback-prompts",
             "start-suggestions", "tips", "lock-screen-tips", "spotlight-ads",
             "ai-assistant", "cortana", "web-search", "widgets"],
            keys);
    }

    // ── The diagnostic data switch, by edition (#2778) ───────────────────
    // It sets AllowTelemetry to 0, which turns diagnostic data off only on Enterprise, Education and Server. Home and
    // Pro read 0 as 1 and still send the required diagnostic data, and the switch promised them none.

    [Theory]
    [InlineData("Enterprise")]
    [InlineData("EnterpriseS")]
    [InlineData("enterprise")]
    [InlineData("Education")]
    [InlineData("ServerStandard")]
    public void OnAnEditionThatCanTurnItOff_TheDiagnosticDataSwitchSaysItStopsIt(string editionId)
        => Assert.Equal(
            ("Disable diagnostic data", "Prevents Windows from sending diagnostic and usage data to Microsoft."),
            PrivacyService.DiagnosticDataWording(editionId));

    [Theory]
    [InlineData("Core", "Home")]
    [InlineData("CoreSingleLanguage", "Home")]
    [InlineData("Professional", "Pro")]
    [InlineData("ProfessionalN", "Pro")]
    [InlineData("ProfessionalWorkstation", "Pro for Workstations")]
    public void OnHomeAndPro_TheDiagnosticDataSwitchSaysItLowersItToTheRequiredLevel(string editionId, string edition)
        => Assert.Equal(("Limit diagnostic data",
                "Lowers the diagnostic data Windows sends to Microsoft to the required level, "
                + $"the least Windows {edition} allows."),
            PrivacyService.DiagnosticDataWording(editionId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ProfessionalEducation")]
    [InlineData("IoTEnterprise")]
    [InlineData("Cloud")]
    public void OnAnEditionItCannotPlace_TheDiagnosticDataSwitchSaysWhatItDoesOnEach(string? editionId)
        => Assert.Equal(("Limit diagnostic data",
                "Lowers the diagnostic data Windows sends to Microsoft as far as your edition allows: "
                + "off on Enterprise and Education, the required level on Home and Pro."),
            PrivacyService.DiagnosticDataWording(editionId));

    /// <summary>
    /// The edition is the one the registry names, read under the HKLM root the service was given: this test's own
    /// key, so the switch is worded for an edition the test chooses and nothing is written outside that key.
    /// </summary>
    [Fact]
    public void LoadToggles_WordsTheDiagnosticDataSwitchForTheEditionTheRegistryNames()
    {
        var rootName = @"Software\SysManagerTests\PrivacyEdition_" + Guid.NewGuid().ToString("N");
        var root = Registry.CurrentUser.CreateSubKey(rootName, writable: true)!;
        try
        {
            var service = new PrivacyService(hkcuRoot: root, hklmRoot: root, ledger: Ledger());
            Assert.Equal(PrivacyService.DiagnosticDataWording(null), Wording(service));

            const string currentVersionPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            using (var currentVersion = root.CreateSubKey(currentVersionPath, writable: true))
                currentVersion.SetValue("EditionID", "Core");

            Assert.Equal(PrivacyService.DiagnosticDataWording("Core"), Wording(service));
            Assert.NotEqual(PrivacyService.DiagnosticDataWording(null), Wording(service));
        }
        finally
        {
            root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(rootName, throwOnMissingSubKey: false);
        }

        static (string, string) Wording(PrivacyService service)
        {
            var toggle = service.LoadToggles().Single(t => t.Key == "diagnostic-data");
            return (toggle.Name, toggle.Description);
        }
    }

    // ── What each switch writes (#2778) ──────────────────────────────────
    // A switch whose value does not do what it says reads back as applied and changes nothing: "Disable Spotlight
    // ads" wrote one of the Settings app's suggestion values, and on Windows 10 the widgets switch wrote Windows 11's
    // policy. These read the definitions over a key of the test's own, which names the build the test chooses.

    private sealed class RedirectedRoot : IDisposable
    {
        private readonly string _name = @"Software\SysManagerTests\PrivacyWrites_" + Guid.NewGuid().ToString("N");
        private readonly string _ledgerDir =
            Path.Combine(Path.GetTempPath(), "SysManagerPrivacyLedgerTests", Guid.NewGuid().ToString("N"));

        public RedirectedRoot(string? build = null)
        {
            Key = Registry.CurrentUser.CreateSubKey(_name, writable: true)!;
            if (build is null) return;
            using var currentVersion = Key.CreateSubKey(InstalledWindows.CurrentVersionPath, writable: true);
            currentVersion.SetValue("CurrentBuildNumber", build);
        }

        public RegistryKey Key { get; }

        public PrivacyService Service() =>
            new(hkcuRoot: Key, hklmRoot: Key, ledger: new PrivacyLedgerService(_ledgerDir));

        public void Dispose()
        {
            Key.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_name, throwOnMissingSubKey: false);
            try { Directory.Delete(_ledgerDir, recursive: true); }
            catch (DirectoryNotFoundException) { /* nothing was recorded */ }
        }
    }

    private const string Cdm = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    /// <summary>
    /// What every switch writes, by key, on Windows 11: its key, its values, and the value that means protected. The
    /// assistant switch in Features is left out; its value is as it was, and its path is pinned by its reach.
    /// </summary>
    [Fact]
    public void EverySwitch_WritesTheValuesItsKeyStandsFor()
    {
        using var root = new RedirectedRoot();
        var written = root.Service().LoadToggles()
            .Where(t => t.Key != "ai-assistant")
            .ToDictionary(t => t.Key, t => (t.RegistryPath, string.Join(" + ", t.ValueNames), t.EnabledValue));

        Assert.Equal(new Dictionary<string, (string, string, int)>
        {
            ["diagnostic-data"] = (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0),
            ["activity-history"] = (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0),
            ["advertising-id"] = (@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
            ["feedback-prompts"] = (@"HKCU\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
            ["start-suggestions"] = (Cdm, "SubscribedContent-338388Enabled", 0),
            ["tips"] = (Cdm, "SoftLandingEnabled", 0),
            ["lock-screen-tips"] = (Cdm, "RotatingLockScreenOverlayEnabled", 0),
            ["spotlight-ads"] = (Cdm,
                "SubscribedContent-338393Enabled + SubscribedContent-353694Enabled"
                + " + SubscribedContent-353696Enabled", 0),
            ["cortana"] = (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0),
            ["web-search"] = (@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
            ["widgets"] = (@"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0),
        }, written);
    }

    /// <summary>
    /// The switch that was "Disable Spotlight ads" says what its values do: they are the Settings app's suggestions,
    /// and the lock screen's promotions are the lock screen tips switch's.
    /// </summary>
    [Fact]
    public void TheSwitchThatWasSpotlightAds_SaysItStopsTheSettingsAppsSuggestions()
    {
        using var root = new RedirectedRoot();
        var toggles = root.Service().LoadToggles();

        var settings = toggles.Single(t => t.Key == "spotlight-ads");
        Assert.Equal("Disable suggestions in Settings", settings.Name);
        Assert.Equal("Stops the Settings app from showing suggested content.", settings.Description);
        Assert.Contains("lock screen", toggles.Single(t => t.Key == "lock-screen-tips").Description,
            StringComparison.Ordinal);
        Assert.DoesNotContain("lock screen", settings.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A switch with several values writes every one of them and puts every one back, and reads as on only when all
    /// of them say so, as Windows' own switch for the Settings app's suggestions sets the three together.
    /// </summary>
    [Fact]
    public void ASwitchWithSeveralValues_WritesAndPutsBackEachOne_AndIsOnOnlyWhenAllAre()
    {
        using var root = new RedirectedRoot();
        var service = root.Service();
        var settings = service.LoadToggles().Single(t => t.Key == "spotlight-ads");
        Assert.False(settings.IsEnabled);

        using (var cdm = root.Key.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                   writable: true))
        {
            cdm.SetValue("SubscribedContent-338393Enabled", 0, RegistryValueKind.DWord);
            cdm.SetValue("SubscribedContent-353694Enabled", 0, RegistryValueKind.DWord);
        }
        Assert.False(Settings().IsEnabled);   // two of the three: Windows still shows the suggestions

        settings.IsEnabled = true;
        Assert.Equal(PrivacyWriteOutcome.Written, service.ApplyToggle(settings));
        Assert.Equal([0, 0, 0], Values());
        Assert.True(Settings().IsEnabled);

        // Back as each was: the two that were there before, and the third gone (#2778).
        settings.IsEnabled = false;
        Assert.Equal(PrivacyWriteOutcome.Written, service.ApplyToggle(settings));
        Assert.Equal([0, 0, null], Values());
        Assert.False(Settings().IsEnabled);

        PrivacyToggle Settings() => service.LoadToggles().Single(t => t.Key == "spotlight-ads");

        object?[] Values()
        {
            using var cdm = root.Key.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager");
            return [.. settings.ValueNames.Select(name => cdm?.GetValue(name))];
        }
    }

    /// <summary>
    /// Windows 10's news and interests has a policy of its own, which Windows 11's widgets policy does not govern, so
    /// the widgets switch writes the one for the Windows it runs on. A build that cannot be read keeps Windows 11's.
    /// </summary>
    [Theory]
    [InlineData("19045", @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds",
        "Disable news and interests")]
    [InlineData("22000", @"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", "Disable widgets")]
    [InlineData("26100", @"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", "Disable widgets")]
    [InlineData(null, @"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", "Disable widgets")]
    [InlineData("not a build", @"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", "Disable widgets")]
    public void TheWidgetsSwitch_WritesThePolicyOfTheWindowsItRunsOn(string? build, string path, string value,
                                                                    string name)
    {
        using var root = new RedirectedRoot(build);

        var widgets = root.Service().LoadToggles().Single(t => t.Key == "widgets");

        Assert.Equal((path, value, 0, name),
            (widgets.RegistryPath, widgets.ValueName, widgets.EnabledValue, widgets.Name));
        Assert.Empty(widgets.AlsoWrites);
    }

    // ── Revert behavior (F07 regression) ──────────────────────────────────
    // Turning a toggle OFF must REMOVE our value (so Windows falls back to its own
    // default), NOT write DisabledValue — which for policy toggles would materialise
    // an enforced GPO the machine never had (e.g. AllowTelemetry=3 = Full telemetry).
    // These use a throwaway HKCU subkey: per-user, no elevation, deleted on dispose.

    private sealed class HkcuTestKey : IDisposable
    {
        // Relative to HKCU. "AllowTelemetry"-style value with an enforcing DisabledValue.
        public string SubPath { get; } = @"Software\SysManagerTests\Privacy_" + Guid.NewGuid().ToString("N");
        public string FullPath => @"HKCU\" + SubPath;

        public PrivacyToggle Toggle(bool isEnabled) => new()
        {
            Key = "test",
            Name = "test",
            Description = "revert test",
            Category = "Telemetry",
            RegistryPath = FullPath,
            ValueName = "AllowTelemetry",
            EnabledValue = 0,   // privacy ON
            DisabledValue = 3,  // the dangerous "enforce Full telemetry" value we must NOT write on revert
            IsEnabled = isEnabled,
        };

        public object? ReadValue()
        {
            using var k = Registry.CurrentUser.OpenSubKey(SubPath);
            return k?.GetValue("AllowTelemetry");
        }

        /// <summary>
        /// Sets the value before the switch is first written, as a policy the user set themselves would be.
        /// </summary>
        public void SetBefore(object value, RegistryValueKind kind)
        {
            using var k = Registry.CurrentUser.CreateSubKey(SubPath, writable: true);
            k.SetValue("AllowTelemetry", value, kind);
        }

        public RegistryValueKind KindNow()
        {
            using var k = Registry.CurrentUser.OpenSubKey(SubPath)!;
            return k.GetValueKind("AllowTelemetry");
        }

        public void Dispose()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(SubPath, throwOnMissingSubKey: false); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void ApplyToggle_On_WritesEnabledValue()
    {
        using var t = new HkcuTestKey();
        var svc = new PrivacyService(ledger: Ledger());

        Assert.Equal(PrivacyWriteOutcome.Written, svc.ApplyToggle(t.Toggle(isEnabled: true)));
        Assert.Equal(0, t.ReadValue()); // EnabledValue written
    }

    [Fact]
    public void ApplyToggle_Off_RemovesValue_DoesNotWriteDisabledValue()
    {
        using var t = new HkcuTestKey();
        var svc = new PrivacyService(ledger: Ledger());

        // First enable (writes 0), then revert (OFF).
        Assert.Equal(PrivacyWriteOutcome.Written, svc.ApplyToggle(t.Toggle(isEnabled: true)));
        Assert.Equal(0, t.ReadValue());

        Assert.Equal(PrivacyWriteOutcome.Written, svc.ApplyToggle(t.Toggle(isEnabled: false)));

        // Regression: the value must be GONE, not set to DisabledValue (3). The old code
        // wrote 3 here — materialising an enforced "Full telemetry" policy on revert.
        Assert.Null(t.ReadValue());
    }

    [Fact]
    public void ApplyToggle_Off_WhenNeverSet_IsIdempotentNoOp()
    {
        using var t = new HkcuTestKey();
        var svc = new PrivacyService(ledger: Ledger());

        // Revert with no prior value present: must succeed and leave the value absent
        // (does not create the key-value with DisabledValue).
        Assert.Equal(PrivacyWriteOutcome.Written, svc.ApplyToggle(t.Toggle(isEnabled: false)));
        Assert.Null(t.ReadValue());
    }

    // ── Putting back what was there (#2778) ──────────────────────────────
    // Turning a switch off removed its value whatever had been there, so a policy the user had set themselves was gone
    // after turning the switch on and off again. What each value held is recorded before the first write.

    [Fact]
    public void AValueThatWasThere_IsPutBack_WhenTheSwitchIsTurnedOffAgain()
    {
        // AllowTelemetry = 1, the required level, set through Group Policy or another tool before SysManager.
        using var t = new HkcuTestKey();
        t.SetBefore(1, RegistryValueKind.DWord);
        var svc = new PrivacyService(ledger: Ledger());

        Assert.Equal(PrivacyWriteOutcome.Written, svc.ApplyToggle(t.Toggle(isEnabled: true)));
        Assert.Equal(0, t.ReadValue());

        Assert.Equal(PrivacyWriteOutcome.Written, svc.ApplyToggle(t.Toggle(isEnabled: false)));
        Assert.Equal(1, t.ReadValue());
        Assert.Equal(RegistryValueKind.DWord, t.KindNow());
    }

    [Fact]
    public void AValueOfAnotherKind_IsPutBackWithItsOwnKind()
    {
        using var t = new HkcuTestKey();
        t.SetBefore("1", RegistryValueKind.String);
        var svc = new PrivacyService(ledger: Ledger());

        svc.ApplyToggle(t.Toggle(isEnabled: true));
        Assert.Equal(PrivacyWriteOutcome.Written, svc.ApplyToggle(t.Toggle(isEnabled: false)));

        Assert.Equal("1", t.ReadValue());
        Assert.Equal(RegistryValueKind.String, t.KindNow());
    }

    [Fact]
    public void TheFirstWritesRecord_IsTheOneKept_SoALaterChangeIsNotPutBack()
    {
        // Applied, then Windows changed the value back, then applied again: off puts back what was there before
        // SysManager's first write, not what Windows had set in between.
        using var t = new HkcuTestKey();
        t.SetBefore(1, RegistryValueKind.DWord);
        var svc = new PrivacyService(ledger: Ledger());

        svc.ApplyToggle(t.Toggle(isEnabled: true));
        t.SetBefore(3, RegistryValueKind.DWord);
        svc.ApplyToggle(t.Toggle(isEnabled: true));
        svc.ApplyToggle(t.Toggle(isEnabled: false));

        Assert.Equal(1, t.ReadValue());
    }

    [Fact]
    public void PuttingAValueBack_ForgetsItsRecord()
    {
        using var t = new HkcuTestKey();
        var svc = new PrivacyService(ledger: Ledger());

        svc.ApplyToggle(t.Toggle(isEnabled: true));
        Assert.Single(Ledger().Read().Records!);
        svc.ApplyToggle(t.Toggle(isEnabled: false));

        Assert.Empty(Ledger().Read().Records!);
    }

    [Fact]
    public void AValueWithNoRecord_IsRemoved_AsBeforeRecordsWereKept()
    {
        // Written by a SysManager that kept no records: there is nothing to put back, so the value goes and Windows
        // falls back to its own default, never to DisabledValue.
        using var t = new HkcuTestKey();
        t.SetBefore(0, RegistryValueKind.DWord);
        var svc = new PrivacyService(ledger: Ledger());

        Assert.Equal(PrivacyWriteOutcome.Written, svc.ApplyToggle(t.Toggle(isEnabled: false)));

        Assert.Null(t.ReadValue());
    }

    [Fact]
    public void ALedgerThatCannotBeRead_LeavesTheValueAsItIs_RatherThanGuess()
    {
        using var t = new HkcuTestKey();
        t.SetBefore(1, RegistryValueKind.DWord);
        var svc = new PrivacyService(ledger: Ledger());
        svc.ApplyToggle(t.Toggle(isEnabled: true));

        using (new FileStream(Path.Combine(_ledgerDir, PrivacyLedgerService.FileName), FileMode.Open, FileAccess.Read,
                   FileShare.None))
        {
            Assert.Equal(PrivacyWriteOutcome.RecordUnreadable, svc.ApplyToggle(t.Toggle(isEnabled: false)));
        }

        Assert.Equal(0, t.ReadValue());   // still SysManager's, and its record still there to put back
        Assert.Equal(PrivacyWriteOutcome.Written, svc.ApplyToggle(t.Toggle(isEnabled: false)));
        Assert.Equal(1, t.ReadValue());
    }

    [Fact]
    public void ALedgerThatDoesNotParse_LeavesTheValueAsItIs_RatherThanGuess()
    {
        using var t = new HkcuTestKey();
        t.SetBefore(1, RegistryValueKind.DWord);
        var svc = new PrivacyService(ledger: Ledger());
        svc.ApplyToggle(t.Toggle(isEnabled: true));
        File.WriteAllText(Path.Combine(_ledgerDir, PrivacyLedgerService.FileName), "{ not a ledger");

        Assert.Equal(PrivacyWriteOutcome.RecordUnreadable, svc.ApplyToggle(t.Toggle(isEnabled: false)));

        Assert.Equal(0, t.ReadValue());
    }

    // ── A key only an administrator can write gets back only a value the setting takes (#2747, #2778) ──
    // The ledger is a file in the user's own profile, which anything running as the user can change.

    private static PrivacyPriorValue Recorded(string? kind, string? data) =>
        new(@"HKLM\SOFTWARE\SysManagerTests", "V", kind, data, null, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("DWord", "0", true)]    // EnabledValue
    [InlineData("DWord", "1", true)]    // DisabledValue
    [InlineData("DWord", "7", false)]   // a value SysManager never sets there
    [InlineData("String", "1", false)]  // the right number in the wrong kind
    [InlineData("Binary", "AQ==", false)]
    [InlineData(null, null, true)]      // there was no value: removing it writes nothing
    public void ASwitchThatNeedsAdministrator_PutsBackOnlyAValueTheSettingTakes(string? kind, string? data, bool may)
    {
        Assert.Equal(may,
            PrivacyService.MayPutBack(FakePrivacy.Toggle("t", on: true, hive: "HKLM"), Recorded(kind, data)));
        Assert.Equal(may,
            PrivacyService.MayPutBack(FakePrivacy.Toggle("t", on: true, policy: true), Recorded(kind, data)));
    }

    [Theory]
    [InlineData("DWord", "7")]
    [InlineData("String", "1")]
    [InlineData("Binary", "AQ==")]
    public void ASwitchForYourOwnSettings_PutsBackWhatWasThere_WhateverItIs(string kind, string data)
        => Assert.True(PrivacyService.MayPutBack(FakePrivacy.Toggle("t", on: true), Recorded(kind, data)));

    [Fact]
    public void TheDiagnosticDataPolicy_TakesItsFourLevels_AndNothingElse()
    {
        using var root = new RedirectedRoot();
        var diagnosticData = root.Service().LoadToggles().Single(t => t.Key == "diagnostic-data");

        Assert.Equal([true, true, true, true, false, false],
            new[] { 0, 1, 2, 3, 4, -1 }.Select(diagnosticData.TakesValue));
    }

    /// <summary>
    /// Through the real diagnostic data switch, whose machine-wide key the redirected root stands in for: the required
    /// level comes back, and a value it never takes, or one of another kind, is removed instead.
    /// </summary>
    [Theory]
    [InlineData(1, RegistryValueKind.DWord, 1)]
    [InlineData(7, RegistryValueKind.DWord, null)]
    [InlineData("1", RegistryValueKind.String, null)]
    public void TheDiagnosticDataSwitch_PutsBackOnlyALevelItTakes(object before, RegistryValueKind kind, object? after)
    {
        using var root = new RedirectedRoot();
        const string policyPath = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
        using (var policy = root.Key.CreateSubKey(policyPath, writable: true))
            policy.SetValue("AllowTelemetry", before, kind);
        var service = root.Service();
        var diagnosticData = service.LoadToggles().Single(t => t.Key == "diagnostic-data");

        diagnosticData.IsEnabled = true;
        Assert.Equal(PrivacyWriteOutcome.Written, service.ApplyToggle(diagnosticData));
        diagnosticData.IsEnabled = false;
        Assert.Equal(PrivacyWriteOutcome.Written, service.ApplyToggle(diagnosticData));

        using var read = root.Key.OpenSubKey(policyPath)!;
        Assert.Equal(after, read.GetValue("AllowTelemetry"));
    }

    [Fact]
    public void WhenWhatWasThereCannotBeRecorded_NothingIsWritten()
    {
        using var t = new HkcuTestKey();
        t.SetBefore(1, RegistryValueKind.DWord);
        var svc = new PrivacyService(ledger: Ledger());
        Directory.CreateDirectory(_ledgerDir);
        File.WriteAllText(Path.Combine(_ledgerDir, PrivacyLedgerService.FileName), "[]");

        using (new FileStream(Path.Combine(_ledgerDir, PrivacyLedgerService.FileName), FileMode.Open, FileAccess.Read,
                   FileShare.None))
        {
            Assert.Equal(PrivacyWriteOutcome.NotRecorded, svc.ApplyToggle(t.Toggle(isEnabled: true)));
        }

        Assert.Equal(1, t.ReadValue());
    }
}
