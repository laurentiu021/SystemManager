// SysManager · StandbyPreferenceServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="StandbyPreferenceService"/> — the persisted auto-purge settings.
/// Every test uses an injected temp directory, so the developer's real preference in
/// %LOCALAPPDATA% is never read or written.
/// </summary>
public class StandbyPreferenceServiceTests : IDisposable
{
    private readonly string _dir;

    public StandbyPreferenceServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerStandbyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private StandbyPreferenceService NewService() => new(_dir);

    // ---------- defaults ----------

    [Fact]
    public void Load_WithNothingSaved_ReturnsSafeDefault()
    {
        var loaded = NewService().Load();

        Assert.False(loaded.AutoPurgeEnabled);
        Assert.Equal(StandbyPreferenceService.DefaultThresholdMb, loaded.ThresholdMb);
    }

    [Fact]
    public void Default_HasAutoPurgeOff()
    {
        // Off is the only safe default: arming an automatic system action by default would purge
        // without the user ever asking.
        Assert.False(StandbyPreferenceService.Default.AutoPurgeEnabled);
    }

    // ---------- round-trip ----------

    [Theory]
    [InlineData(true, 2048)]
    [InlineData(false, 512)]
    [InlineData(true, 64)]      // exactly the minimum
    public void Save_ThenLoad_ReturnsSameSettings(bool enabled, double threshold)
    {
        NewService().Save(new StandbyPreference(enabled, threshold));

        // A second instance, as after an app restart — which is the whole point of persisting.
        var loaded = NewService().Load();

        Assert.Equal(enabled, loaded.AutoPurgeEnabled);
        Assert.Equal(threshold, loaded.ThresholdMb);
    }

    [Fact]
    public void Save_Twice_LastWriteWins()
    {
        var svc = NewService();
        svc.Save(new StandbyPreference(true, 2048));
        svc.Save(new StandbyPreference(false, 4096));

        var loaded = svc.Load();

        Assert.False(loaded.AutoPurgeEnabled);
        Assert.Equal(4096, loaded.ThresholdMb);
    }

    [Fact]
    public void Save_CreatesTheConfigDirectoryIfMissing()
    {
        var nested = Path.Combine(_dir, "does", "not", "exist", "yet");

        new StandbyPreferenceService(nested).Save(new StandbyPreference(true, 2048));

        Assert.True(new StandbyPreferenceService(nested).Load().AutoPurgeEnabled);
    }

    // ---------- rejecting untrustworthy input ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    public void Parse_InvalidInput_ReturnsDefault(string? json)
    {
        var parsed = StandbyPreferenceService.Parse(json);

        Assert.False(parsed.AutoPurgeEnabled);
        Assert.Equal(StandbyPreferenceService.DefaultThresholdMb, parsed.ThresholdMb);
    }

    [Theory]
    [InlineData(0)]           // would make ShouldAutoPurge never fire
    [InlineData(-500)]        // nonsense
    [InlineData(1)]           // below the floor: would fire almost continuously
    [InlineData(63)]          // just below the floor
    [InlineData(2_000_000)]   // above any real machine's RAM: would fire on every tick
    public void Parse_OutOfRangeThreshold_FallsBackToTheDefault(double threshold)
    {
        var json = $"{{\"AutoPurgeEnabled\":true,\"ThresholdMb\":{threshold}}}";

        var parsed = StandbyPreferenceService.Parse(json);

        // The toggle is still honoured — only the unusable number is replaced, so a corrupt
        // threshold does not silently disarm a feature the user turned on.
        Assert.True(parsed.AutoPurgeEnabled);
        Assert.Equal(StandbyPreferenceService.DefaultThresholdMb, parsed.ThresholdMb);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Parse_NonFiniteThreshold_FallsBackToTheDefault(string literal)
    {
        var json = $"{{\"AutoPurgeEnabled\":true,\"ThresholdMb\":\"{literal}\"}}";

        var parsed = StandbyPreferenceService.Parse(json);

        Assert.Equal(StandbyPreferenceService.DefaultThresholdMb, parsed.ThresholdMb);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(1024)]
    [InlineData(1_048_576)]   // exactly the ceiling
    public void Parse_InRangeThreshold_IsHonoured(double threshold)
    {
        var json = $"{{\"AutoPurgeEnabled\":false,\"ThresholdMb\":{threshold}}}";

        Assert.Equal(threshold, StandbyPreferenceService.Parse(json).ThresholdMb);
    }

    [Fact]
    public void Load_MalformedFileOnDisk_ReturnsDefaultWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(_dir, "standby-preference.json"), "{ not valid json");

        var loaded = NewService().Load();

        Assert.False(loaded.AutoPurgeEnabled);
        Assert.Equal(StandbyPreferenceService.DefaultThresholdMb, loaded.ThresholdMb);
    }

    // ---------- serialization shape ----------

    [Fact]
    public void Serialize_RoundTripsThroughParse()
    {
        var original = new StandbyPreference(true, 3072);

        var parsed = StandbyPreferenceService.Parse(StandbyPreferenceService.Serialize(original));

        Assert.Equal(original, parsed);
    }

    [Fact]
    public void ShouldAutoPurge_AgreesWithAPersistedThreshold()
    {
        // The persisted value has to be usable by the purge decision it exists to drive, so the
        // two are checked together rather than in isolation.
        NewService().Save(new StandbyPreference(true, 2048));
        var loaded = NewService().Load();

        Assert.True(ViewModels.StandbyMemoryViewModel.ShouldAutoPurge(1024, loaded.ThresholdMb));
        Assert.False(ViewModels.StandbyMemoryViewModel.ShouldAutoPurge(4096, loaded.ThresholdMb));
    }
}
