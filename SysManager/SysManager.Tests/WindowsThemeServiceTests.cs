// SysManager · WindowsThemeServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class WindowsThemeServiceTests
{
    private static TimeOnly T(int h, int m) => new(h, m);

    // Overnight window: dark 19:00 → light 07:00 (dark spans midnight).
    [Theory]
    [InlineData(19, 0, true)]    // exactly dark start → dark
    [InlineData(23, 30, true)]   // late evening → dark
    [InlineData(2, 0, true)]     // after midnight → still dark
    [InlineData(6, 59, true)]    // just before light → dark
    [InlineData(7, 0, false)]    // exactly light start → light
    [InlineData(7, 1, false)]    // morning → light
    [InlineData(12, 0, false)]   // midday → light
    [InlineData(18, 59, false)]  // just before dark → light
    public void ShouldBeDark_OvernightWindow(int h, int m, bool expectedDark)
        => Assert.Equal(expectedDark, WindowsThemeService.ShouldBeDark(T(h, m), T(19, 0), T(7, 0)));

    // Same-day window: dark 02:00 → light 07:00 (no wrap).
    [Theory]
    [InlineData(1, 59, false)]   // before dark → light
    [InlineData(2, 0, true)]     // exactly dark start → dark
    [InlineData(5, 0, true)]     // within window → dark
    [InlineData(7, 0, false)]    // exactly light start → light
    [InlineData(20, 0, false)]   // evening (outside same-day window) → light
    public void ShouldBeDark_SameDayWindow(int h, int m, bool expectedDark)
        => Assert.Equal(expectedDark, WindowsThemeService.ShouldBeDark(T(h, m), T(2, 0), T(7, 0)));

    [Fact]
    public void ShouldBeDark_EqualTimes_AlwaysLight()
    {
        // Degenerate/empty window → never auto-dark (no-op schedule).
        Assert.False(WindowsThemeService.ShouldBeDark(T(8, 0), T(8, 0), T(8, 0)));
        Assert.False(WindowsThemeService.ShouldBeDark(T(20, 0), T(8, 0), T(8, 0)));
    }
}

/// <summary>
/// The dark-mode schedule on disk, in a temp directory, so the user's own schedule in %AppData% is never read or
/// written.
/// </summary>
public class WindowsThemeServiceScheduleTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "SysManagerDarkModeScheduleTests", Guid.NewGuid().ToString("N"));

    public WindowsThemeServiceScheduleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
        GC.SuppressFinalize(this);
    }

    private string ScheduleFile => Path.Combine(_dir, "darkmode-schedule.json");

    private WindowsThemeService NewService() => new(_dir);

    private static DarkModeSchedule Evenings() => new() { Enabled = true, DarkStart = "20:00", LightStart = "06:30" };

    [Fact]
    public void SaveSchedule_ThenLoad_ReturnsTheSameSchedule()
    {
        Assert.True(NewService().SaveSchedule(Evenings()));

        var loaded = NewService().LoadSchedule();

        Assert.True(loaded.Enabled);
        Assert.Equal("20:00", loaded.DarkStart);
        Assert.Equal("06:30", loaded.LightStart);
    }

    // A schedule that could not be read when the tab opened was written over by the first change, with the
    // defaults on screen in place of the schedule the file held (#2521).

    [Fact]
    public void SaveSchedule_AfterALoadThatCouldNotReadTheFile_WritesNothing()
    {
        NewService().SaveSchedule(Evenings());
        var before = File.ReadAllBytes(ScheduleFile);
        var service = NewService();

        // Held with delete sharing only while it loads: the read fails.
        using (new FileStream(ScheduleFile, FileMode.Open, FileAccess.Read, FileShare.Delete))
            Assert.False(service.LoadSchedule().Enabled);

        Assert.False(service.SaveSchedule(new DarkModeSchedule { Enabled = false }));
        Assert.Equal(before, File.ReadAllBytes(ScheduleFile));
    }

    [Fact]
    public void SaveSchedule_OverAFileThatDoesNotParse_KeepsItAside_ThenSaves()
    {
        File.WriteAllText(ScheduleFile, "{ not a schedule");
        var service = NewService();
        service.LoadSchedule();

        Assert.True(service.SaveSchedule(Evenings()));
        Assert.True(service.SaveSchedule(Evenings()));

        Assert.Equal("{ not a schedule", File.ReadAllText(ScheduleFile + ".unreadable"));
        Assert.False(File.Exists(ScheduleFile + ".unreadable-2"), "the file the first save wrote was set aside too");
        Assert.Equal("20:00", NewService().LoadSchedule().DarkStart);
    }

    [Fact]
    public void SaveSchedule_WhenAFileThatDoesNotParseCannotBeSetAside_WritesNothing()
    {
        File.WriteAllText(ScheduleFile, "{ not a schedule");
        // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not.
        Directory.CreateDirectory(ScheduleFile + ".unreadable");
        var service = NewService();
        service.LoadSchedule();

        Assert.False(service.SaveSchedule(Evenings()));
        Assert.Equal("{ not a schedule", File.ReadAllText(ScheduleFile));
    }
}
