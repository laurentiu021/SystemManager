// SysManager · ClosePreferenceServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="ClosePreferenceService"/> — the persisted answer to the
/// window close prompt. Every test uses an injected temp directory so the developer's
/// real preference in %LOCALAPPDATA% is never read or written.
/// </summary>
public class ClosePreferenceServiceTests : IDisposable
{
    private readonly string _dir;

    public ClosePreferenceServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerCloseTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private ClosePreferenceService NewService() => new(_dir);

    // ---------- default state ----------

    [Fact]
    public void Load_WithNothingSaved_ReturnsAsk()
    {
        Assert.Equal(CloseBehavior.Ask, NewService().Load());
    }

    // ---------- round-trip ----------

    [Theory]
    [InlineData(CloseBehavior.MinimizeToTray)]
    [InlineData(CloseBehavior.Exit)]
    public void Save_ThenLoad_ReturnsSameBehavior(CloseBehavior behavior)
    {
        NewService().Save(behavior);

        // A second instance, as after an app restart — the point of persisting at all.
        Assert.Equal(behavior, NewService().Load());
    }

    [Fact]
    public void Save_Ask_ClearsAnyStoredChoice()
    {
        var svc = NewService();
        svc.Save(CloseBehavior.Exit);
        Assert.Equal(CloseBehavior.Exit, svc.Load());

        svc.Save(CloseBehavior.Ask);

        Assert.Equal(CloseBehavior.Ask, svc.Load());
        Assert.False(File.Exists(Path.Combine(_dir, "close-preference.json")));
    }

    [Fact]
    public void Save_Twice_LastChoiceWins()
    {
        var svc = NewService();
        svc.Save(CloseBehavior.MinimizeToTray);
        svc.Save(CloseBehavior.Exit);

        Assert.Equal(CloseBehavior.Exit, svc.Load());
    }

    // ---------- rejecting untrustworthy input ----------
    //
    // Every negative case must land on Ask, never on a concrete action: asking again is
    // harmless, whereas trusting a damaged file could silently exit an app the user
    // wanted left running in the tray.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Behavior\":null}")]
    [InlineData("{\"Behavior\":\"\"}")]
    [InlineData("{\"Behavior\":\"Nonsense\"}")]
    [InlineData("{\"SomethingElse\":\"Exit\"}")]
    public void Parse_InvalidInput_ReturnsAsk(string? json)
    {
        Assert.Equal(CloseBehavior.Ask, ClosePreferenceService.Parse(json));
    }

    [Fact]
    public void Parse_ExplicitAskValue_ReturnsAsk()
    {
        // "Ask" is never written by Save, but a hand-edited file could contain it and must
        // not be mistaken for a real choice.
        Assert.Equal(CloseBehavior.Ask, ClosePreferenceService.Parse("{\"Behavior\":\"Ask\"}"));
    }

    [Theory]
    [InlineData("minimizetotray", CloseBehavior.MinimizeToTray)]
    [InlineData("MINIMIZETOTRAY", CloseBehavior.MinimizeToTray)]
    [InlineData("exit", CloseBehavior.Exit)]
    public void Parse_IsCaseInsensitive(string value, CloseBehavior expected)
    {
        Assert.Equal(expected, ClosePreferenceService.Parse("{\"Behavior\":\"" + value + "\"}"));
    }

    [Fact]
    public void Load_MalformedFileOnDisk_ReturnsAskWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(_dir, "close-preference.json"), "{ this is not valid json");

        Assert.Equal(CloseBehavior.Ask, NewService().Load());
    }

    // ---------- serialization shape ----------

    [Fact]
    public void Serialize_RoundTripsThroughParse()
    {
        var json = ClosePreferenceService.Serialize(CloseBehavior.MinimizeToTray);

        Assert.Contains("MinimizeToTray", json);
        Assert.Equal(CloseBehavior.MinimizeToTray, ClosePreferenceService.Parse(json));
    }

    [Fact]
    public void Save_CreatesTheConfigDirectoryIfMissing()
    {
        var nested = Path.Combine(_dir, "does", "not", "exist", "yet");
        var svc = new ClosePreferenceService(nested);

        svc.Save(CloseBehavior.Exit);

        Assert.Equal(CloseBehavior.Exit, new ClosePreferenceService(nested).Load());
    }
}

/// <summary>
/// What closing the window resolves to. This lives outside <c>MainWindow.OnClosing</c> precisely so it
/// can be asserted: the Exit branch used to fall through to <c>base.OnClosing</c> without calling
/// <c>Shutdown</c>, and because <c>App</c> sets <c>ShutdownMode.OnExplicitShutdown</c> the process kept
/// running with no window and no tray icon — the icon is disposed in <c>App.OnExit</c>, which nothing had
/// triggered. The single-instance mutex stayed held, so the next launch handed itself to an invisible
/// instance and quit, and the remembered answer made it recur on every launch (#1827).
/// </summary>
public class CloseDecisionTests
{
    [Fact]
    public void RememberedExit_EndsTheProcess()
    {
        // The defect. Nothing else in the app exits on the user's behalf, so this must resolve to a
        // shutdown and not merely to "close the window".
        Assert.Equal(
            CloseAction.ExitApplication,
            CloseDecision.Resolve(CloseBehavior.Exit, answer: null));
    }

    [Fact]
    public void RememberedTray_HidesInsteadOfExiting()
        => Assert.Equal(
            CloseAction.HideToTray,
            CloseDecision.Resolve(CloseBehavior.MinimizeToTray, answer: null));

    [Theory]
    [InlineData(CloseChoice.Exit, CloseAction.ExitApplication)]
    [InlineData(CloseChoice.MinimizeToTray, CloseAction.HideToTray)]
    [InlineData(CloseChoice.Cancel, CloseAction.KeepOpen)]
    public void WithNothingRemembered_TheAnswerDecides(CloseChoice answer, CloseAction expected)
        => Assert.Equal(expected, CloseDecision.Resolve(CloseBehavior.Ask, answer));

    [Fact]
    public void ARememberedAnswerIsNotOverriddenByAStaleChoice()
    {
        // A remembered preference must win outright. If the stored value were ever ignored in favour of
        // an answer, a user who chose "keep running" would be exited by a leftover value.
        Assert.Equal(
            CloseAction.HideToTray,
            CloseDecision.Resolve(CloseBehavior.MinimizeToTray, CloseChoice.Exit));
        Assert.Equal(
            CloseAction.ExitApplication,
            CloseDecision.Resolve(CloseBehavior.Exit, CloseChoice.MinimizeToTray));
    }

    [Fact]
    public void UnansweredPrompt_LeavesTheWindowOpenRatherThanExiting()
    {
        // "We could not ask" is not consent to quit. Losing a window is recoverable; exiting unasked
        // during an operation is not — so the unknown case must be the harmless one.
        Assert.Equal(
            CloseAction.KeepOpen,
            CloseDecision.Resolve(CloseBehavior.Ask, answer: null));
    }

    [Theory]
    [InlineData(CloseChoice.Exit, CloseBehavior.Exit)]
    [InlineData(CloseChoice.MinimizeToTray, CloseBehavior.MinimizeToTray)]
    public void AnAnsweredPromptIsRemembered(CloseChoice answer, CloseBehavior expected)
        => Assert.Equal(expected, CloseDecision.PreferenceToSave(answer));

    [Fact]
    public void CancellingRemembersNothing()
    {
        // Cancel means "not now", not a preference. Saving anything here would silently answer the
        // question for the user and they would never be asked again.
        Assert.Null(CloseDecision.PreferenceToSave(CloseChoice.Cancel));
    }

    [Fact]
    public void EveryChoiceAndBehaviourIsResolved()
    {
        // Total by construction: no combination may fall through to an unintended arm. The old switch
        // relied on a `default:` meaning Exit, which is safe only as long as every other member is
        // listed above it — a new CloseChoice member would silently have become "exit the app".
        foreach (var behavior in Enum.GetValues<CloseBehavior>())
        {
            Assert.Contains(CloseDecision.Resolve(behavior, null), Enum.GetValues<CloseAction>());
            foreach (var choice in Enum.GetValues<CloseChoice>())
                Assert.Contains(CloseDecision.Resolve(behavior, choice), Enum.GetValues<CloseAction>());
        }
    }
}
