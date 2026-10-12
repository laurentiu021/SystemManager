// SysManager · FirstRunServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The record that keeps the Welcome screen to the first launch on a PC (#1635), in a temp folder so nothing here
/// touches the user's own.
/// </summary>
public sealed class FirstRunServiceTests : IDisposable
{
    private static readonly DateTimeOffset Seen = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);

    private readonly string _dir;

    public FirstRunServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone */ }
    }

    private FirstRunService NewService(string? dir = null) => new(dir ?? _dir, new FixedClock());

    private string RecordFile => Path.Combine(_dir, FirstRunService.FileName);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Seen;
    }

    [Fact]
    public void WithNothingRecorded_TheWelcomeHasNotBeenSeen()
    {
        Assert.False(NewService().HasSeenWelcome());
        Assert.Equal(RecordFile, NewService().FilePath);
    }

    [Fact]
    public void OnceMarked_ANewInstanceSaysItHasBeenSeen()
    {
        Assert.True(NewService().MarkWelcomeSeen());

        // A second instance, as after a restart, which is the whole point of the record.
        Assert.True(NewService().HasSeenWelcome());
    }

    [Fact]
    public void TheRecordSaysWhenTheWelcomeWasSeen()
    {
        NewService().MarkWelcomeSeen();

        using var json = JsonDocument.Parse(File.ReadAllText(RecordFile));
        Assert.Equal(Seen, json.RootElement.GetProperty("SeenUtc").GetDateTimeOffset());
    }

    [Fact]
    public void Marking_CreatesTheFolderWhenItIsNotThereYet()
    {
        var nested = Path.Combine(_dir, "not", "there", "yet");

        Assert.True(NewService(nested).MarkWelcomeSeen());

        Assert.True(NewService(nested).HasSeenWelcome());
    }

    /// <summary>
    /// Only the app writes the record, and it writes it when the Welcome screen goes, so a damaged one still means the
    /// screen was seen. Reading it as unseen would show the screen on every launch for as long as the file stayed so.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"SeenUtc\":")]
    public void ARecordThatDoesNotParse_StillMeansTheWelcomeWasSeen(string content)
    {
        File.WriteAllText(RecordFile, content);

        Assert.True(NewService().HasSeenWelcome());
    }

    [Fact]
    public void ARecordThatCannotBeWritten_IsReportedAsFailed_WithoutThrowing()
    {
        // A file where the folder should be: creating the folder fails, as it does on a profile that cannot be written.
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "a file, not a folder");

        var service = NewService(blocked);

        Assert.False(service.MarkWelcomeSeen());
        Assert.False(service.HasSeenWelcome());
    }
}
