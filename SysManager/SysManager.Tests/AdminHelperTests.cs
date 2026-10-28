// SysManager · AdminHelperTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="AdminHelper"/>. These are safe to run on CI
/// (non-admin) and on dev boxes (admin or not).
/// </summary>
// Serialized: the ForceElevation tests replace AdminHelper's elevation probe.
[Collection("ProcessWideStatics")]
public class AdminHelperTests
{
    [Fact]
    public void IsElevated_IsConsistentAcrossCalls()
    {
        // Elevation state cannot change during the test process's lifetime, so two
        // calls must agree. (The former IsElevated_ReturnsBoolean test asserted
        // Assert.IsType<bool> on a bool-returning method — always true, tested nothing —
        // and was folded into this real invariant.)
        var a = AdminHelper.IsElevated();
        var b = AdminHelper.IsElevated();
        Assert.Equal(a, b);
    }

    /// <summary>
    /// A forced scope decides what <see cref="AdminHelper.IsElevated"/> answers, and disposing it puts the
    /// real probe back.
    /// </summary>
    /// <remarks>
    /// This is the contract every other elevation test leans on, and nothing else can observe it. A leaked
    /// override is invisible to the tests that use one, because each forces its own value first, and
    /// invisible to the tests that compare a view-model against <c>IsElevated()</c>, because both sides read
    /// the same leaked probe and therefore still agree. So the restore has to be asserted here or nowhere:
    /// making <c>ElevationScope.Dispose</c> a no-op left the whole suite green until this test existed.
    /// </remarks>
    [Fact]
    public void ForceElevation_DecidesTheAnswer_AndRestoresTheRealProbeOnDispose()
    {
        var real = AdminHelper.IsElevated();

        using (AdminHelper.ForceElevation(true))
            Assert.True(AdminHelper.IsElevated());

        Assert.Equal(real, AdminHelper.IsElevated());

        using (AdminHelper.ForceElevation(false))
            Assert.False(AdminHelper.IsElevated());

        Assert.Equal(real, AdminHelper.IsElevated());
    }

    /// <summary>
    /// Nested scopes restore the ENCLOSING value, not the real probe.
    /// </summary>
    /// <remarks>
    /// Restoring the default instead of the previous probe would look correct in every single-scope test and
    /// break only where one forced test calls a helper that forces again — the case that is hardest to
    /// debug, because the outer scope silently stops applying halfway through its own body.
    /// <para>Both directions, and that is not symmetry for its own sake. With only the true-outside-false
    /// nesting, a <c>Dispose</c> that restored the real probe would still satisfy the outer assertion on an
    /// elevated host, so the test would pin the contract on a developer's box and pass vacuously on CI.
    /// Running the mirror too means one of the two outer assertions contradicts the real probe whichever
    /// way the host happens to be.</para>
    /// </remarks>
    [Fact]
    public void ForceElevation_Nested_RestoresTheEnclosingScope_NotTheRealProbe()
    {
        using (AdminHelper.ForceElevation(true))
        {
            using (AdminHelper.ForceElevation(false))
                Assert.False(AdminHelper.IsElevated());

            Assert.True(AdminHelper.IsElevated());
        }

        using (AdminHelper.ForceElevation(false))
        {
            using (AdminHelper.ForceElevation(true))
                Assert.True(AdminHelper.IsElevated());

            Assert.False(AdminHelper.IsElevated());
        }
    }

    [Fact]
    public void RelaunchedElevatedArg_IsAStableNonEmptySwitch()
    {
        // App.OnStartup matches this exact token in the elevated child's command line to
        // decide whether to wait for the single-instance mutex handover. It must stay a
        // non-empty, whitespace-free switch so it survives argument splitting intact.
        Assert.False(string.IsNullOrWhiteSpace(AdminHelper.RelaunchedElevatedArg));
        Assert.DoesNotContain(' ', AdminHelper.RelaunchedElevatedArg);
        Assert.StartsWith("--", AdminHelper.RelaunchedElevatedArg);
    }

    [Fact]
    public void RelaunchAsAdmin_DoesNotThrow()
    {
        // On CI / non-interactive hosts this will fail to launch (no UAC)
        // but must not throw — it returns false instead.
        // On dev boxes it may actually launch a UAC prompt, but the test
        // process won't wait for it.
        var ex = Record.Exception(() => AdminHelper.RelaunchAsAdmin());
        Assert.Null(ex);
    }

    [Fact]
    public void RelaunchAsAdmin_WithArgumentHint_DoesNotThrow()
    {
        var ex = Record.Exception(() => AdminHelper.RelaunchAsAdmin("--tab=network"));
        Assert.Null(ex);
    }

    // Removed RelaunchAsAdmin_ReturnsBoolean: it asserted Assert.IsType<bool> on a
    // bool-returning method (always true, tested nothing) while needlessly invoking the
    // side-effecting relaunch a third time. RelaunchAsAdmin_DoesNotThrow already covers
    // the call.

    /// <summary>
    /// The hint must reach the elevated child through <c>ArgumentList</c>, never concatenated into the
    /// <c>Arguments</c> string. Concatenation lets a hint containing a space split into several
    /// arguments — silently breaking the "return to the right tab" contract, and appending switches to
    /// a process that is about to run with administrator rights.
    /// <para>Asserted against the source, because observing the real command line would mean starting
    /// an elevated process and therefore a UAC prompt. No caller passes a hint today, so this guards
    /// the first one that does.</para>
    /// </summary>
    [Fact]
    public void RelaunchAsAdmin_PassesTheHintAsAnArgument_NotAsConcatenatedText()
    {
        var source = File.ReadAllText(TestPaths.AppFile("Helpers", "AdminHelper.cs"));
        var start = source.IndexOf("public static bool RelaunchAsAdmin", StringComparison.Ordinal);
        Assert.True(start >= 0, "RelaunchAsAdmin not found — update this guard.");
        var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Assert.Contains("ArgumentList.Add", body, StringComparison.Ordinal);

        // The specific defect: the hint interpolated beside the sentinel in a single string, and the
        // Arguments property being set at all.
        Assert.DoesNotContain("{RelaunchedElevatedArg} {", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments =", body, StringComparison.Ordinal);

        // The sentinel must still be passed, or the elevated child is treated as a duplicate and the
        // user is left looking at the non-elevated window.
        Assert.Contains("ArgumentList.Add(RelaunchedElevatedArg)", body, StringComparison.Ordinal);
    }

    // ── elevated as a different account (#2826) ──────────────────────────────

    /// <summary>Sets both probes, and restores both on dispose — the two-probe equivalent of ForceElevation.</summary>
    private sealed class ForcedAccountState : IDisposable
    {
        private readonly IDisposable _elevation;
        private readonly Func<string?> _previousSid;

        public ForcedAccountState(bool elevated, string? interactiveSid)
        {
            _elevation = AdminHelper.ForceElevation(elevated);
            _previousSid = AdminHelper.InteractiveSessionSidProbe;
            AdminHelper.InteractiveSessionSidProbe = () => interactiveSid;
        }

        public void Dispose()
        {
            AdminHelper.InteractiveSessionSidProbe = _previousSid;
            _elevation.Dispose();
        }
    }

    private static string OwnSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    [Fact]
    public void IsElevatedAsAnotherUser_WhenNotElevated_IsFalse()
    {
        // Not elevated at all is never "elevated as another user" — the family-PC scenario this exists for
        // only happens once "Run as administrator" has already been accepted.
        using var _ = new ForcedAccountState(elevated: false, interactiveSid: "S-1-5-21-0-0-0-9999");
        Assert.False(AdminHelper.IsElevatedAsAnotherUser());
    }

    [Fact]
    public void IsElevatedAsAnotherUser_WhenElevatedAndSidsMatch_IsFalse()
    {
        // The ordinary case: a standard account elevates WITH ITS OWN password (or is already an
        // administrator), so the elevated copy runs as the same account that is signed in.
        using var _ = new ForcedAccountState(elevated: true, interactiveSid: OwnSid());
        Assert.False(AdminHelper.IsElevatedAsAnotherUser());
    }

    [Fact]
    public void IsElevatedAsAnotherUser_WhenElevatedAndSidsDiffer_IsTrue()
    {
        // The family-PC case: elevated with a DIFFERENT administrator's password.
        using var _ = new ForcedAccountState(elevated: true, interactiveSid: "S-1-5-21-0-0-0-9999");
        Assert.True(AdminHelper.IsElevatedAsAnotherUser());
    }

    [Fact]
    public void IsElevatedAsAnotherUser_WhenTheInteractiveAccountIsUnknown_FailsSafeToFalse()
    {
        // No interactive session (a service session, or WTS could not say) must not be read as "different
        // account" — there is nothing to redirect to, so claiming a mismatch here would be a guess.
        using var _ = new ForcedAccountState(elevated: true, interactiveSid: null);
        Assert.False(AdminHelper.IsElevatedAsAnotherUser());
    }

    [Fact]
    public void PerUserRegistryRoot_WhenNotElevatedAsAnotherUser_IsTheRealCurrentUserHive()
    {
        using var _ = new ForcedAccountState(elevated: false, interactiveSid: null);
        Assert.Equal(Registry.CurrentUser.Name, AdminHelper.PerUserRegistryRoot().Name);
    }

    [Fact]
    public void PerUserRegistryRoot_WhenElevatedAsAnotherUser_ButTheSidCannotBeResolved_FallsBackLogged()
    {
        // The rare failure case: elevated as another account, but WTS could not say who is signed in.
        // Falls back to this account's own hive rather than leaving the service unable to do anything —
        // degraded, not broken, and the fallback is logged rather than silent.
        using var _ = new ForcedAccountState(elevated: true, interactiveSid: null);
        Assert.Equal(Registry.CurrentUser.Name, AdminHelper.PerUserRegistryRoot().Name);
    }

    [Fact]
    public void PerUserRegistryRoot_WhenElevatedAsAnotherUser_ButTheHiveIsNotLoaded_FallsBackLogged()
    {
        // A SID that resolves to a syntactically valid string but has no loaded hive under HKEY_USERS —
        // Registry.Users.OpenSubKey must return null cleanly for this, not throw, and the fallback still
        // applies rather than leaving PerUserRegistryRoot unable to return anything.
        using var _ = new ForcedAccountState(elevated: true, interactiveSid: "S-1-5-21-0-0-0-9999");
        Assert.Equal(Registry.CurrentUser.Name, AdminHelper.PerUserRegistryRoot().Name);
    }

    /// <summary>
    /// The actual redirect attempt — <c>Registry.Users.OpenSubKey</c> on the interactive SID — must still be
    /// there on the elevated-as-another-user branch. Asserted against the source because every behavioural
    /// test above happens to land on the SAME fallback value (<see cref="Registry.CurrentUser"/>) a real
    /// signed-in SECOND account is needed to prove the success path, which this suite cannot create — so a
    /// future change that silently dropped the redirect attempt and always returned the fallback would pass
    /// every one of them.
    /// </summary>
    [Fact]
    public void PerUserRegistryRoot_StillActuallyTriesTheInteractiveAccountsHive()
    {
        var source = File.ReadAllText(TestPaths.AppFile("Helpers", "AdminHelper.cs"));
        var start = source.IndexOf("public static RegistryKey PerUserRegistryRoot", StringComparison.Ordinal);
        Assert.True(start >= 0, "PerUserRegistryRoot not found — update this guard.");
        var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Assert.Contains("Registry.Users.OpenSubKey", body, StringComparison.Ordinal);
        Assert.Contains("InteractiveSessionSid()", body, StringComparison.Ordinal);
    }

    // ── each per-user service's default constructor actually calls PerUserRegistryRoot (#2826) ──

    [Theory]
    [InlineData("Services", "PrivacyService.cs", "AdminHelper.PerUserRegistryRoot()")]
    [InlineData("Services", "PrivacyMonitorService.cs", "AdminHelper.PerUserRegistryRoot()")]
    [InlineData("Services", "NotificationBlockerService.cs", "AdminHelper.PerUserRegistryRoot()")]
    [InlineData("Services", "MaintenanceSchedulerService.cs", "AdminHelper.IsElevatedAsAnotherUser()")]
    public void EachPerUserService_ActuallyConsultsTheSignedInAccount(string folder, string file, string mustContain)
    {
        // Asserted against the source, not behaviourally: the real redirect needs a second real signed-in
        // account this suite cannot create. Each default constructor must still name the helper rather than
        // defaulting straight to Registry.CurrentUser / WindowsIdentity.GetCurrent() again.
        var source = File.ReadAllText(TestPaths.AppFile(folder, file));
        Assert.Contains(mustContain, source, StringComparison.Ordinal);
    }
}
