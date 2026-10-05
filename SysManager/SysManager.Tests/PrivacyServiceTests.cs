// SysManager · PrivacyServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PrivacyService"/>'s apply-result signalling (regression for
/// the "false success on a swallowed write failure" defect). Uses toggles whose
/// registry path has an unrecognized hive so <c>ApplyToggle</c> returns false WITHOUT
/// touching the real registry — deterministic and side-effect-free.
/// </summary>
public class PrivacyServiceTests
{
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
    public void ApplyToggle_UnwritableHive_ReturnsFalse()
    {
        var svc = new PrivacyService();
        Assert.False(svc.ApplyToggle(BadHiveToggle()));
    }

    [Fact]
    public void ApplyAll_ReturnsTheTogglesThatFailed()
    {
        var svc = new PrivacyService();
        var a = BadHiveToggle("a");
        var b = BadHiveToggle("b");

        var failed = svc.ApplyAll([a, b]);

        // Both writes fail (unrecognized hive) so both come back in the failed list —
        // the caller must not treat them as applied.
        Assert.Equal(2, failed.Count);
        Assert.Contains(a, failed);
        Assert.Contains(b, failed);
    }

    [Fact]
    public void ApplyToggle_NullToggle_Throws()
    {
        var svc = new PrivacyService();
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
        var keys = new PrivacyService().LoadToggles().Select(t => t.Key);

        Assert.Equal(
            ["diagnostic-data", "activity-history", "advertising-id", "feedback-prompts",
             "start-suggestions", "tips", "lock-screen-tips", "spotlight-ads",
             "ai-assistant", "cortana", "web-search", "widgets"],
            keys);
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

        public void Dispose()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(SubPath, throwOnMissingSubKey: false); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void ApplyToggle_On_WritesEnabledValue()
    {
        using var t = new HkcuTestKey();
        var svc = new PrivacyService();

        Assert.True(svc.ApplyToggle(t.Toggle(isEnabled: true)));
        Assert.Equal(0, t.ReadValue()); // EnabledValue written
    }

    [Fact]
    public void ApplyToggle_Off_RemovesValue_DoesNotWriteDisabledValue()
    {
        using var t = new HkcuTestKey();
        var svc = new PrivacyService();

        // First enable (writes 0), then revert (OFF).
        Assert.True(svc.ApplyToggle(t.Toggle(isEnabled: true)));
        Assert.Equal(0, t.ReadValue());

        Assert.True(svc.ApplyToggle(t.Toggle(isEnabled: false)));

        // Regression: the value must be GONE, not set to DisabledValue (3). The old code
        // wrote 3 here — materialising an enforced "Full telemetry" policy on revert.
        Assert.Null(t.ReadValue());
    }

    [Fact]
    public void ApplyToggle_Off_WhenNeverSet_IsIdempotentNoOp()
    {
        using var t = new HkcuTestKey();
        var svc = new PrivacyService();

        // Revert with no prior value present: must succeed and leave the value absent
        // (does not create the key-value with DisabledValue).
        Assert.True(svc.ApplyToggle(t.Toggle(isEnabled: false)));
        Assert.Null(t.ReadValue());
    }
}
