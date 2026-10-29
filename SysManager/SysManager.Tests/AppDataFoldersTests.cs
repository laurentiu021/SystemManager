// SysManager · AppDataFoldersTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="AppDataFolders"/> and the pairs that used to compose
/// <c>%LocalAppData%\SysManager</c>/<c>%AppData%\SysManager</c> independently and had to agree by
/// coincidence (#2860): the crash marker's writer in <c>App</c> and its reader in
/// <see cref="CrashMarkerService"/>, the update folder in <see cref="UpdateService"/> and
/// <see cref="UpdateApplier"/>, and <c>ILeftoverEnvironment.OwnFolders</c>.
/// </summary>
public class AppDataFoldersTests
{
    [Fact]
    public void LocalAndRoaming_AreRedirectedAwayFromTheRealProfile_ForThisTestRun()
    {
        // TestAssemblyInit redirects both before any test runs. If this ever stopped working, every
        // other test in this class — and every service's own default — would silently start reading
        // and writing the real profile instead of a temp folder.
        Assert.Contains("SysManagerTestRun", AppDataFolders.Local, StringComparison.Ordinal);
        Assert.Contains("SysManagerTestRun", AppDataFolders.Roaming, StringComparison.Ordinal);
        Assert.NotEqual(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager"),
            AppDataFolders.Local);
    }

    [Theory]
    [InlineData(nameof(AppDataFolders.Logs), "logs")]
    [InlineData(nameof(AppDataFolders.Updates), "updates")]
    [InlineData(nameof(AppDataFolders.Tools), "tools")]
    [InlineData(nameof(AppDataFolders.IconCache), "IconCache")]
    [InlineData(nameof(AppDataFolders.Backups), "Backups")]
    public void EveryLocalSubFolder_IsRootedUnderLocal(string propertyName, string expectedLeaf)
    {
        var property = typeof(AppDataFolders).GetProperty(propertyName, BindingFlags.NonPublic | BindingFlags.Static)!;
        var value = (string)property.GetValue(null)!;
        Assert.StartsWith(AppDataFolders.Local, value, StringComparison.Ordinal);
        Assert.Equal(expectedLeaf, Path.GetFileName(value));
    }

    [Fact]
    public void CrashMarkerService_OverAppDataFoldersLocal_FindsWhatWasWrittenThere()
    {
        // App.WriteCrashMarker and CrashMarkerService's own default both resolve through
        // AppDataFolders.Local now, instead of composing %LocalAppData%\SysManager independently —
        // passed explicitly here (rather than relying on the default) because
        // ArchitectureTests.Tests_NeverLeaveAConfigDirAtTheRealProfile requires every test construction
        // of a configDir-seamed type to pass a value, on principle, even one that is now safe by default.
        var path = Path.Combine(AppDataFolders.Local, "last-crash.json");
        Directory.CreateDirectory(AppDataFolders.Local);
        File.WriteAllText(path, """{"WhenUtc":"2026-01-01T00:00:00Z","Version":"1.0.0","ExceptionType":"System.Exception","Message":"m"}""");
        try
        {
            var marker = new CrashMarkerService(AppDataFolders.Local)
                .TakePending(new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero));
            Assert.NotNull(marker);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UpdateApplier_PreviousBuildPath_WithNoOverride_ResolvesUnderAppDataFoldersUpdates()
    {
        var resolved = UpdateApplier.PreviousBuildPath(null);
        Assert.StartsWith(AppDataFolders.Updates, resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void LeftoverEnvironment_OwnFolders_AgreeWithAppDataFolders()
    {
        var env = new SystemLeftoverEnvironment();
        Assert.Equal(AppDataFolders.Local, env.OwnFolders[0]);
        Assert.Equal(AppDataFolders.Roaming, env.OwnFolders[1]);
    }
}
