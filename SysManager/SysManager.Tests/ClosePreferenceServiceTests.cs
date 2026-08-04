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
