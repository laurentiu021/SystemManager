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
/// <para>Every tweak and service here also gets a <see cref="TempConfig"/>. Left at its default, the
/// write-count ledger they read and write is the real one under <c>%LocalAppData%\SysManager</c>, and one
/// test here incremented it on every run of the suite (#2555).</para>
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
        using var config = new TempConfig();
        Seed(null);

        var result = await new NotificationsTweak(originalToastEnabled: null, _root, configDir: config.Path)
            .ApplyAsync(default);

        Assert.Equal(GamingTweakResult.Applied, result);
        Assert.Equal(0, Current());
    }

    [Fact]
    public async Task Apply_WhenAlreadySuppressed_ReportsNoChangeAndWritesNothing()
    {
        // No key at all, so a stray write would be visible as the key coming into existence.
        using var config = new TempConfig();
        var result = await new NotificationsTweak(originalToastEnabled: 0, _root, configDir: config.Path)
            .ApplyAsync(default);

        Assert.Equal(GamingTweakResult.NoChange, result);
        using var key = _root.OpenSubKey(NotificationBlockerService.PushKeyPath);
        Assert.Null(key);
    }

    [Fact]
    public async Task Revert_WhenTheValueWasAbsent_RemovesItAgain()
    {
        using var config = new TempConfig();
        Seed(null);
        var tweak = new NotificationsTweak(originalToastEnabled: null, _root, configDir: config.Path);
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
        using var config = new TempConfig();
        Seed(1);
        var tweak = new NotificationsTweak(originalToastEnabled: 1, _root, configDir: config.Path);
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
        using var config = new TempConfig();
        Seed(1);
        var tweak = new NotificationsTweak(originalToastEnabled: 1, _root, configDir: config.Path);
        await tweak.ApplyAsync(default);
        Assert.Equal(0, Current());

        // The user re-enables notifications from the Notifications tab, mid-session.
        new NotificationBlockerService(_root, config.Path).SetGlobalToastEnabled(true);
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
        using var config = new TempConfig();
        Seed(null);
        var tweak = new NotificationsTweak(originalToastEnabled: null, _root, configDir: config.Path);
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

        var atApply = NotificationBlockerService.ReadMainToggleWriteCount(config.Path);
        var tweak = new NotificationsTweak(
            originalToastEnabled: null, baseKey: _root, writeCountAtApply: atApply, configDir: config.Path);
        await tweak.ApplyAsync(CancellationToken.None);
        Assert.Equal(0, Current());                     // the profile silenced them

        // The user opens Privacy & Security -> Notifications and turns the main toggle off herself.
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

        var atApply = NotificationBlockerService.ReadMainToggleWriteCount(config.Path);
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

        var before = NotificationBlockerService.ReadMainToggleWriteCount(config.Path);
        new NotificationBlockerService(_root, config.Path).SetGlobalToastEnabled(false);
        var after = NotificationBlockerService.ReadMainToggleWriteCount(config.Path);

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

        Assert.Equal(3, NotificationBlockerService.ReadMainToggleWriteCount(config.Path));
    }

    /// <summary>A missing ledger reads as 0 rather than throwing: nothing has been counted yet.</summary>
    [Fact]
    public void AMissingLedger_ReadsAsZeroRatherThanThrowing()
    {
        using var config = new TempConfig();

        Assert.Equal(0, NotificationBlockerService.ReadMainToggleWriteCount(config.Path));
    }

    // ── A ledger that cannot be read (#2538) ─────────────────────────────
    // It read as 0, which differs from every real count, so a revert took it for the user muting notifications
    // and left them muted after the game. The ledger is held open with delete sharing only while its read must
    // fail: the read fails, and the replace a write ends with still succeeds.

    private static string LedgerFile(TempConfig config) =>
        Path.Combine(config.Path, NotificationBlockerService.WriteLedgerFileName);

    private static FileStream HoldAgainstReads(TempConfig config) =>
        new(LedgerFile(config), FileMode.Open, FileAccess.Read, FileShare.Delete);

    /// <summary>Two earlier toggles on the Notifications tab, leaving notifications on and the count at 2.</summary>
    private void CountTwoWrites(TempConfig config)
    {
        var service = new NotificationBlockerService(_root, config.Path);
        service.SetGlobalToastEnabled(false);
        service.SetGlobalToastEnabled(true);
        Assert.Equal(2, NotificationBlockerService.ReadMainToggleWriteCount(config.Path));
        Assert.Null(Current());
    }

    [Fact]
    public void ALedgerThatCannotBeRead_IsUnknown_NotZero()
    {
        using var config = new TempConfig();
        CountTwoWrites(config);

        using (HoldAgainstReads(config))
            Assert.Null(NotificationBlockerService.ReadMainToggleWriteCount(config.Path));

        File.WriteAllText(LedgerFile(config), "{ not a ledger");
        Assert.Null(NotificationBlockerService.ReadMainToggleWriteCount(config.Path));
    }

    [Fact]
    public async Task Revert_WhenTheLedgerCannotBeReadAtRevert_RestoresTheSnapshot()
    {
        using var config = new TempConfig();
        CountTwoWrites(config);
        var tweak = new NotificationsTweak(
            originalToastEnabled: null, baseKey: _root,
            writeCountAtApply: NotificationBlockerService.ReadMainToggleWriteCount(config.Path), configDir: config.Path);
        await tweak.ApplyAsync(CancellationToken.None);
        Assert.Equal(0, Current());

        using (HoldAgainstReads(config))
            await tweak.RevertAsync(CancellationToken.None);

        Assert.Null(Current());                         // nobody touched the toggle, so notifications are back
    }

    [Fact]
    public async Task Revert_WhenTheLedgerDoesNotParseAtRevert_RestoresTheSnapshot()
    {
        using var config = new TempConfig();
        CountTwoWrites(config);
        var tweak = new NotificationsTweak(
            originalToastEnabled: null, baseKey: _root,
            writeCountAtApply: NotificationBlockerService.ReadMainToggleWriteCount(config.Path), configDir: config.Path);
        await tweak.ApplyAsync(CancellationToken.None);

        File.WriteAllText(LedgerFile(config), "{ not a ledger");
        await tweak.RevertAsync(CancellationToken.None);

        Assert.Null(Current());
    }

    [Fact]
    public async Task Revert_WhenTheLedgerCouldNotBeReadAtApply_RestoresTheSnapshot()
    {
        using var config = new TempConfig();
        CountTwoWrites(config);

        int? atApply;
        using (HoldAgainstReads(config))
            atApply = NotificationBlockerService.ReadMainToggleWriteCount(config.Path);
        var tweak = new NotificationsTweak(
            originalToastEnabled: null, baseKey: _root, writeCountAtApply: atApply, configDir: config.Path);
        await tweak.ApplyAsync(CancellationToken.None);

        await tweak.RevertAsync(CancellationToken.None);   // the ledger reads 2 again now

        Assert.Null(Current());
    }

    /// <summary>
    /// The user's own mute is still kept when the ledger could not be read at the moment they made it.
    /// </summary>
    /// <remarks>
    /// The write side counts from 0 and writes 1 over a ledger it could not read. Writing nothing instead
    /// would leave the count at 2 and make the revert undo the mute.
    /// </remarks>
    [Fact]
    public async Task Revert_WhenTheUsersMuteCouldNotReadTheLedger_StillLeavesThemMuted()
    {
        using var config = new TempConfig();
        CountTwoWrites(config);
        var tweak = new NotificationsTweak(
            originalToastEnabled: null, baseKey: _root,
            writeCountAtApply: NotificationBlockerService.ReadMainToggleWriteCount(config.Path), configDir: config.Path);
        await tweak.ApplyAsync(CancellationToken.None);

        using (HoldAgainstReads(config))
            Assert.True(new NotificationBlockerService(_root, config.Path).SetGlobalToastEnabled(false));

        await tweak.RevertAsync(CancellationToken.None);

        Assert.Equal(0, Current());
    }
}
