// SysManager · PrivacyServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
}
