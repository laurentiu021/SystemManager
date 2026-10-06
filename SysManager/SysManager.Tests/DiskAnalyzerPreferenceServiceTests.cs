// SysManager · DiskAnalyzerPreferenceServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="DiskAnalyzerPreferenceService"/>, which remembers whether the Disk Analyzer map is shown
/// (#1592). Every test uses an injected temp directory, so the real preference in %LOCALAPPDATA% is never read or
/// written.
/// </summary>
public class DiskAnalyzerPreferenceServiceTests : IDisposable
{
    private readonly string _dir;

    public DiskAnalyzerPreferenceServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerDiskPrefTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private DiskAnalyzerPreferenceService NewService() => new(_dir);

    private string PreferenceFile => Path.Combine(_dir, DiskAnalyzerPreferenceService.FileName);

    [Fact]
    public void Load_WithNothingSaved_ShowsTheMap()
        => Assert.True(NewService().Load().ShowMap);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Save_ThenLoad_InANewInstance_ReturnsTheSameSetting(bool showMap)
    {
        Assert.True(NewService().Save(new DiskAnalyzerPreference(showMap)));

        // A second instance, as after an app restart, which is the whole point of persisting.
        Assert.Equal(showMap, NewService().Load().ShowMap);
    }

    [Fact]
    public void Save_CreatesTheConfigDirectoryIfMissing()
    {
        var nested = Path.Combine(_dir, "does", "not", "exist", "yet");

        Assert.True(new DiskAnalyzerPreferenceService(nested).Save(new DiskAnalyzerPreference(false)));

        Assert.False(new DiskAnalyzerPreferenceService(nested).Load().ShowMap);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    public void Parse_InputThatIsNotSettings_ShowsTheMap(string? json)
        => Assert.True(DiskAnalyzerPreferenceService.Parse(json).ShowMap);

    [Fact]
    public void Serialize_RoundTripsThroughParse()
    {
        var original = new DiskAnalyzerPreference(ShowMap: false);

        Assert.Equal(original, DiskAnalyzerPreferenceService.Parse(DiskAnalyzerPreferenceService.Serialize(original)));
    }

    [Fact]
    public void Load_AMalformedFileOnDisk_ShowsTheMap_WithoutThrowing()
    {
        File.WriteAllText(PreferenceFile, "{ not valid json");

        Assert.True(NewService().Load().ShowMap);
    }

    // ---------- a file that could not be read or parsed (#2521) ----------

    [Fact]
    public void Save_AfterALoadThatCouldNotReadTheFile_WritesNothing()
    {
        NewService().Save(new DiskAnalyzerPreference(false));
        var before = File.ReadAllBytes(PreferenceFile);
        var svc = NewService();

        // Held with delete sharing only while it loads: the read fails.
        using (new FileStream(PreferenceFile, FileMode.Open, FileAccess.Read, FileShare.Delete))
            Assert.Equal(DiskAnalyzerPreferenceService.Default, svc.Load());

        Assert.False(svc.Save(new DiskAnalyzerPreference(true)));
        Assert.Equal(before, File.ReadAllBytes(PreferenceFile));
        Assert.False(NewService().Load().ShowMap);
    }

    [Fact]
    public void Save_OverAFileThatDoesNotParse_KeepsItAside_ThenSaves()
    {
        File.WriteAllText(PreferenceFile, "{ not valid json");
        var svc = NewService();
        svc.Load();

        Assert.True(svc.Save(new DiskAnalyzerPreference(false)));
        Assert.True(svc.Save(new DiskAnalyzerPreference(true)));

        Assert.Equal("{ not valid json", File.ReadAllText(PreferenceFile + ".unreadable"));
        Assert.False(File.Exists(PreferenceFile + ".unreadable-2"), "the file the first save wrote was set aside too");
        Assert.True(NewService().Load().ShowMap);
    }

    [Fact]
    public void Save_WhenAFileThatDoesNotParseCannotBeSetAside_WritesNothing()
    {
        File.WriteAllText(PreferenceFile, "{ not valid json");
        // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not.
        Directory.CreateDirectory(PreferenceFile + ".unreadable");
        var svc = NewService();
        svc.Load();

        Assert.False(svc.Save(new DiskAnalyzerPreference(false)));
        Assert.Equal("{ not valid json", File.ReadAllText(PreferenceFile));
    }
}
