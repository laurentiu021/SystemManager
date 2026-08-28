// SysManager · NotificationsTweakTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Round-trip tests for the Gaming Profile <c>NotificationsTweak</c>, which had none: the profile
/// engine's own tests drive fake <see cref="IGamingTweak"/> steps, so nothing exercised the registry
/// write this tweak really performs. The tweak now takes an injectable registry root, so the
/// apply/revert round-trip runs against a disposable HKCU subkey instead of the machine's real
/// notification settings (mirrors <see cref="NotificationBlockerServiceTests"/>).
/// </summary>
public sealed class NotificationsTweakTests : IDisposable
{
    private readonly string _rootName = @"Software\SysManagerTests\NotifTweak_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public NotificationsTweakTests()
        => _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); }
        catch (System.Security.SecurityException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    /// <summary>The raw ToastEnabled value under the redirected root; null when absent.</summary>
    private object? Current()
    {
        using var key = _root.OpenSubKey(NotificationBlockerService.PushKeyPath);
        return key?.GetValue(NotificationBlockerService.ToastValueName);
    }

    private void Seed(int? value)
    {
        using var key = _root.CreateSubKey(NotificationBlockerService.PushKeyPath, writable: true)!;
        if (value is { } v) key.SetValue(NotificationBlockerService.ToastValueName, v, RegistryValueKind.DWord);
        else key.DeleteValue(NotificationBlockerService.ToastValueName, throwOnMissingValue: false);
    }

    [Fact]
    public async Task Apply_WhenNotificationsWereOn_SuppressesThem()
    {
        Seed(null);

        var result = await new NotificationsTweak(originalToastEnabled: null, _root).ApplyAsync(default);

        Assert.Equal(GamingTweakResult.Applied, result);
        Assert.Equal(0, Current());
    }

    [Fact]
    public async Task Apply_WhenAlreadySuppressed_ReportsNoChangeAndWritesNothing()
    {
        // No key at all, so a stray write would be visible as the key coming into existence.
        var result = await new NotificationsTweak(originalToastEnabled: 0, _root).ApplyAsync(default);

        Assert.Equal(GamingTweakResult.NoChange, result);
        using var key = _root.OpenSubKey(NotificationBlockerService.PushKeyPath);
        Assert.Null(key);
    }

    [Fact]
    public async Task Revert_WhenTheValueWasAbsent_RemovesItAgain()
    {
        Seed(null);
        var tweak = new NotificationsTweak(originalToastEnabled: null, _root);
        await tweak.ApplyAsync(default);
        Assert.Equal(0, Current());

        await tweak.RevertAsync(default);

        Assert.Null(Current());
    }

    /// <summary>
    /// An explicit 1 must come back as 1, not as "absent". Windows treats both as notifications-on,
    /// so the two are easy to conflate — but the tweak documents an exact restore, and routing this
    /// through a boolean set-enabled API would quietly turn the 1 into a deletion. This test is what
    /// makes that difference observable.
    /// </summary>
    [Fact]
    public async Task Revert_WhenTheValueWasExplicitlyOne_RestoresOneRatherThanDeletingIt()
    {
        Seed(1);
        var tweak = new NotificationsTweak(originalToastEnabled: 1, _root);
        await tweak.ApplyAsync(default);
        Assert.Equal(0, Current());

        await tweak.RevertAsync(default);

        Assert.Equal(1, Current());
    }

    /// <summary>
    /// Regression test for the reverting half of #1502. The Notifications tab writes the SAME value
    /// this tweak writes, so a user can switch notifications back on while a profile is still active.
    /// Revert used to restore the pre-game snapshot unconditionally, which overturned that newer
    /// decision — notifications went silent again on their own when the game exited. Revert now only
    /// undoes the 0 it wrote, so a value that is no longer 0 is left exactly as the user left it.
    /// </summary>
    [Fact]
    public async Task Revert_WhenTheUserTurnedNotificationsBackOnMidSession_KeepsTheirChoice()
    {
        // The snapshot is an explicit 1 rather than "absent" on purpose: the Notifications tab
        // re-enables by DELETING the value, so a snapshot of 1 is what makes the old unconditional
        // restore observable. With a null snapshot both the bug and the fix leave the value absent,
        // and the test would pass either way — proving nothing.
        Seed(1);
        var tweak = new NotificationsTweak(originalToastEnabled: 1, _root);
        await tweak.ApplyAsync(default);
        Assert.Equal(0, Current());

        // The user re-enables notifications from the Notifications tab, mid-session.
        new NotificationBlockerService(_root).SetGlobalToastEnabled(true);
        Assert.Null(Current());

        await tweak.RevertAsync(default);

        Assert.Null(Current());
    }

    /// <summary>
    /// Same rule when the user's mid-session value is an explicit 1 rather than a deletion: revert
    /// must not overwrite it with the snapshot either.
    /// </summary>
    [Fact]
    public async Task Revert_WhenTheUserSetAnExplicitOneMidSession_KeepsIt()
    {
        Seed(null);
        var tweak = new NotificationsTweak(originalToastEnabled: null, _root);
        await tweak.ApplyAsync(default);

        Seed(1);

        await tweak.RevertAsync(default);

        Assert.Equal(1, Current());
    }

    [Fact]
    public void ReadToastEnabled_WhenTheValueIsAbsent_IsNull()
    {
        Seed(null);

        Assert.Null(NotificationsTweak.ReadToastEnabled(_root));
    }

    [Fact]
    public void ReadToastEnabled_WhenSuppressed_IsZero()
    {
        Seed(0);

        Assert.Equal(0, NotificationsTweak.ReadToastEnabled(_root));
    }
    // ── Who wrote the 0 (#1948) ────────────────────────────────────────────────

    /// <summary>A disposable config directory, so the ledger never touches the real LocalAppData copy.</summary>
    private sealed class TempConfig : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "SysManagerTests_Ledger_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// The sequence this fix exists for: notifications on, profile applied, then the user mutes them
    /// HERSELF mid-session, then the profile reverts.
    /// </summary>
    /// <remarks>
    /// The registry cannot answer this on its own — the user's 0 and the tweak's 0 are byte-identical — so
    /// the value-comparison guard cannot help and revert used to restore "absent", silently re-enabling
    /// notifications the user had just deliberately turned off.
    /// <para>Driven through the real <see cref="NotificationBlockerService"/> rather than by seeding the
    /// registry directly. Writing the value by hand would exercise the comparison while proving nothing
    /// about whether the Notifications tab actually records its write, which is the half that can rot.</para>
    /// </remarks>
    [Fact]
    public async Task Revert_WhenTheUserMutedNotificationsMidSession_LeavesThemMuted()
    {
        using var config = new TempConfig();
        Seed(null);                                     // notifications on: value absent

        var atApply = NotificationBlockerService.ReadMasterToggleWriteCount(config.Path);
        var tweak = new NotificationsTweak(
            originalToastEnabled: null, baseKey: _root, writeCountAtApply: atApply, configDir: config.Path);
        await tweak.ApplyAsync(CancellationToken.None);
        Assert.Equal(0, Current());                     // the profile silenced them

        // The user opens Privacy & Security -> Notifications and turns the master toggle off herself.
        new NotificationBlockerService(_root, config.Path).SetGlobalToastEnabled(false);

        await tweak.RevertAsync(CancellationToken.None);

        Assert.Equal(0, Current());
    }

    /// <summary>
    /// The ordinary case must still revert. Without this, "never restore when the value is 0" would pass the
    /// test above while making the tweak permanent.
    /// </summary>
    [Fact]
    public async Task Revert_WhenNobodyElseTouchedTheToggle_RestoresTheSnapshot()
    {
        using var config = new TempConfig();
        Seed(null);

        var atApply = NotificationBlockerService.ReadMasterToggleWriteCount(config.Path);
        var tweak = new NotificationsTweak(
            originalToastEnabled: null, baseKey: _root, writeCountAtApply: atApply, configDir: config.Path);
        await tweak.ApplyAsync(CancellationToken.None);
        Assert.Equal(0, Current());

        await tweak.RevertAsync(CancellationToken.None);

        Assert.Null(Current());                         // back to absent = notifications on
    }

    /// <summary>
    /// A snapshot written by a build from before this field existed has no count, and must revert exactly as
    /// it did then rather than refusing to.
    /// </summary>
    /// <remarks>
    /// This is the upgrade path: a session left active by an older build is reverted by the new one, and the
    /// safe failure is "behave like before", not "never restore".
    /// </remarks>
    [Fact]
    public async Task Revert_WithNoCapturedCount_FallsBackToTheOldBehaviour()
    {
        using var config = new TempConfig();
        Seed(null);

        var tweak = new NotificationsTweak(
            originalToastEnabled: null, baseKey: _root, writeCountAtApply: null, configDir: config.Path);
        await tweak.ApplyAsync(CancellationToken.None);

        // Even a user write cannot be detected without a captured baseline, so the snapshot is restored.
        new NotificationBlockerService(_root, config.Path).SetGlobalToastEnabled(false);

        await tweak.RevertAsync(CancellationToken.None);

        Assert.Null(Current());
    }

    /// <summary>
    /// The ledger has to survive the process, because revert can run in a later one: an ActiveSession left
    /// behind by a run that closed mid-game is reverted on the next launch.
    /// </summary>
    /// <remarks>
    /// Simulated by reading the count through a fresh service instance over the same directory, which is
    /// what a second process would do. An in-memory marker would pass every test above and fail here.
    /// </remarks>
    [Fact]
    public void TheWriteLedger_SurvivesTheProcessThatWroteIt()
    {
        using var config = new TempConfig();

        var before = NotificationBlockerService.ReadMasterToggleWriteCount(config.Path);
        new NotificationBlockerService(_root, config.Path).SetGlobalToastEnabled(false);
        var after = NotificationBlockerService.ReadMasterToggleWriteCount(config.Path);

        Assert.Equal(before + 1, after);
    }

    /// <summary>Every user write moves the count, so two writes cannot look like none.</summary>
    [Fact]
    public void TheWriteLedger_CountsEveryWrite()
    {
        using var config = new TempConfig();
        var service = new NotificationBlockerService(_root, config.Path);

        service.SetGlobalToastEnabled(false);
        service.SetGlobalToastEnabled(true);
        service.SetGlobalToastEnabled(false);

        Assert.Equal(3, NotificationBlockerService.ReadMasterToggleWriteCount(config.Path));
    }

    /// <summary>
    /// A missing or unreadable ledger reads as 0 rather than throwing, and 0 equals the captured 0, so a lost
    /// file degrades to the behaviour that shipped before the ledger existed instead of to never reverting.
    /// </summary>
    [Fact]
    public void AMissingLedger_ReadsAsZeroRatherThanThrowing()
    {
        using var config = new TempConfig();

        Assert.Equal(0, NotificationBlockerService.ReadMasterToggleWriteCount(config.Path));
    }
}
