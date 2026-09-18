// SysManager · AppBlockerUnrecoverableTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// A block an OLDER build already wrote is recognised, described, and never silently repaired.
/// </summary>
/// <remarks>
/// #2030 stopped the app CREATING an IFEO block on <c>consent.exe</c>. It did nothing for a machine that
/// already had one, and that is the case that strands somebody: unblocking writes to HKLM, which needs
/// elevation, which needs the consent UI the block disabled. Such an entry used to appear in the blocked
/// list as an ordinary row beside the deliberate ones (#2357).
/// <para>The damage is reproduced the way it actually happened — the value written STRAIGHT INTO the hive,
/// bypassing <see cref="AppBlockerService.TryBlockApp"/>, because today's code refuses to write it. A test
/// that went through the service could not set this state up at all, which is precisely why the gap
/// survived: the only supported path already refuses, so nothing exercised the unsupported one.</para>
/// </remarks>
public sealed class AppBlockerUnrecoverableTests : IDisposable
{
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    // Mirrors the service's own derivation rather than hardcoding C:\Windows\System32: the value has to
    // match byte for byte or GetBlockedApps treats it as somebody else's debugger and skips it.
    private static readonly string BlockerDebugger =
        System.IO.Path.Combine(Environment.SystemDirectory, "SysManager_Blocked.exe");

    private readonly string _rootName = @"Software\SysManagerTests\Unrecoverable_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public AppBlockerUnrecoverableTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
        _root.CreateSubKey(IfeoPath, writable: true)!.Dispose();
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Writes an IFEO entry directly, the way a build published before #2030 would have.</summary>
    private void PlantBlock(string exeName, string? debugger)
    {
        using var key = _root.CreateSubKey($@"{IfeoPath}\{exeName}", writable: true)!;
        if (debugger is not null) key.SetValue("Debugger", debugger, RegistryValueKind.String);
    }

    private AppBlockerService Service(string? ownName = "SysManager.exe") => new(_root, ownExecutableName: ownName);

    // ── the predicate, one definition shared by refusal and classification ──────────────────────────

    [Theory]
    [InlineData("consent.exe")]
    [InlineData("CONSENT.EXE")]
    [InlineData("consent")]          // bare name — .exe is appended before the lookup
    [InlineData("  consent.exe  ")]  // a registry value can arrive padded
    [InlineData("winlogon.exe")]
    [InlineData("lsass")]
    public void IsElevationOrBootCritical_NormalisesTheNameTheSameWayTheBlockPathDoes(string name)
    {
        // The refusal in TryBlockApp and the classification of an existing block now read ONE list. If
        // they read two, the second would drift and stop recognising exactly the entries that matter —
        // which is the whole reason the predicate was extracted rather than copied.
        Assert.True(AppBlockerService.IsElevationOrBootCritical(name));
    }

    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("steam.exe")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsElevationOrBootCritical_OrdinaryOrAbsentName_IsNotCritical(string? name)
    {
        Assert.False(AppBlockerService.IsElevationOrBootCritical(name));
    }

    // ── classification of blocks that already exist ─────────────────────────────────────────────────

    [Fact]
    public void GetBlockedApps_ConsentBlockedByAnOlderBuild_IsFlaggedUnrecoverable()
    {
        PlantBlock("consent.exe", BlockerDebugger);

        var app = Assert.Single(Service().GetBlockedApps());

        Assert.Equal("consent.exe", app.ExecutableName);
        Assert.True(app.IsUnrecoverable,
            "consent.exe is blocked and the app reported it as an ordinary entry. Unblocking it writes to "
            + "HKLM, which needs the elevation prompt this block disabled — so listing it without a mark is "
            + "how somebody is left with a button that cannot work and no explanation.");
    }

    [Fact]
    public void GetBlockedApps_AnOrdinaryBlock_IsNotFlagged()
    {
        // The other half. A flag that fires on everything says nothing, and the banner it drives would
        // then accuse a deliberate block of having cost the user their permission prompt.
        PlantBlock("notepad.exe", BlockerDebugger);

        var app = Assert.Single(Service().GetBlockedApps());

        Assert.Equal("notepad.exe", app.ExecutableName);
        Assert.False(app.IsUnrecoverable);
    }

    [Fact]
    public void GetBlockedApps_TheAppsOwnExecutable_IsFlaggedWhileItCanStillBeFixed()
    {
        // Blocking our own name is refused now, but a block written under it by an older build takes
        // effect at the NEXT launch — so the app is still running and can still undo it. Saying nothing
        // means the user finds the app simply gone tomorrow.
        PlantBlock("SysManager.exe", BlockerDebugger);

        var app = Assert.Single(Service(ownName: "SysManager.exe").GetBlockedApps());

        Assert.True(app.IsUnrecoverable);
    }

    [Fact]
    public void GetBlockedApps_OwnNameUnresolved_DoesNotFlagEverything()
    {
        // ResolveOwnExecutableName can fail and returns null, which disables the self-guard. That must
        // not turn into "no own name, so treat the entry as fine" for consent.exe, nor into flagging
        // an unrelated app.
        PlantBlock("notepad.exe", BlockerDebugger);
        PlantBlock("consent.exe", BlockerDebugger);

        var apps = Service(ownName: null).GetBlockedApps();

        Assert.False(Assert.Single(apps, a => a.ExecutableName == "notepad.exe").IsUnrecoverable);
        Assert.True(Assert.Single(apps, a => a.ExecutableName == "consent.exe").IsUnrecoverable);
    }

    // ── what must NOT be reported ───────────────────────────────────────────────────────────────────

    [Fact]
    public void GetBlockedApps_AnEmptyIfeoKey_IsNotReportedAtAll()
    {
        // The state a machine is left in once the Debugger value alone is deleted, and the state a real
        // machine was found in after exactly that repair. An IFEO subkey with no Debugger has NO effect on
        // Windows, so reporting it would raise a permanent unfixable warning about nothing — and the entry
        // cannot be removed without administrator rights, so the warning would never clear.
        PlantBlock("consent.exe", debugger: null);

        Assert.Empty(Service().GetBlockedApps());
    }

    [Fact]
    public void GetBlockedApps_SomebodyElsesDebugger_IsNotReported()
    {
        // A real debugger attached by a developer tool. Not ours, not our business, and claiming it would
        // tell the user SysManager broke something it never touched.
        PlantBlock("consent.exe", @"C:\Tools\somedebugger.exe");

        Assert.Empty(Service().GetBlockedApps());
    }

    [Fact]
    public void GetBlockedApps_MixedHive_FlagsOnlyTheStrandedOne()
    {
        // The realistic shape of an affected machine: one deliberate block, one accident, one key left
        // behind by a partial repair, one foreign debugger. Only the accident may be flagged, and only
        // the two of ours may be listed at all.
        PlantBlock("notepad.exe", BlockerDebugger);
        PlantBlock("consent.exe", BlockerDebugger);
        PlantBlock("winlogon.exe", debugger: null);
        PlantBlock("chrome.exe", @"C:\Tools\somedebugger.exe");

        var apps = Service().GetBlockedApps();

        Assert.Equal(2, apps.Count);
        Assert.Single(apps, a => a.IsUnrecoverable);
        Assert.Equal("consent.exe", Assert.Single(apps, a => a.IsUnrecoverable).ExecutableName);
    }

    // ── the guard must not have quietly weakened the refusal it was extracted from ──────────────────

    [Fact]
    public void TryBlockApp_StillRefusesEveryNameTheClassifierCallsCritical()
    {
        // Extracting the predicate could have changed WHICH names TryBlockApp refuses without any single
        // test noticing, because the existing refusal tests each name one entry. This walks the whole set
        // through the public block path: refusal and classification must agree on every member, in both
        // directions, or one of the two is reading a list the other does not.
        var svc = Service();
        string[] set =
        [
            "winlogon.exe", "wininit.exe", "csrss.exe", "smss.exe", "services.exe",
            "lsass.exe", "lsaiso.exe", "fontdrvhost.exe", "dwm.exe", "logonui.exe",
            "explorer.exe", "svchost.exe", "ctfmon.exe", "userinit.exe", "spoolsv.exe",
            "consent.exe",
        ];

        Assert.Equal(16, set.Length);

        foreach (var name in set)
        {
            Assert.True(AppBlockerService.IsElevationOrBootCritical(name),
                $"{name} is refused by TryBlockApp but the classifier does not call it critical, so a "
                + "block an older build wrote on it would be listed as ordinary.");
            Assert.Equal(AppBlockerService.BlockResult.BootCritical, svc.TryBlockApp(name));
        }

        // And nothing was added to the refusal list by accident: a name outside the set must still block.
        Assert.Equal(AppBlockerService.BlockResult.Success, svc.TryBlockApp("notepad.exe"));
    }
}
