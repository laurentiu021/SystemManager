// SysManager · AppAlertHistoryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// New App Alerts' detections, kept between sessions (#1507), in a temp folder so nothing here touches the user's
/// own list.
/// </summary>
public sealed class AppAlertHistoryTests : IDisposable
{
    private readonly string _dir;

    public AppAlertHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone */ }
    }

    private AppAlertHistory NewHistory() => new(_dir);

    private string StoreFile => Path.Combine(_dir, "app-alerts.json");

    private static AppInstallEntry Alert(string name, int minute = 0, bool acknowledged = false) => new()
    {
        Name = name,
        Publisher = "Contoso",
        InstallPath = @"C:\Program Files\Contoso",
        DetectedAt = new DateTime(2026, 10, 5, 15, minute, 0),
        Source = "Registry",
        IsAcknowledged = acknowledged,
    };

    [Fact]
    public void WithNothingKept_TheListIsEmpty_AndReadable()
    {
        var read = NewHistory().Load();

        Assert.True(read.Readable);
        Assert.Empty(read.Alerts);
        Assert.Equal(Path.Combine(_dir, "app-alerts.json"), NewHistory().FilePath);
    }

    [Fact]
    public void ADetection_IsKept_WithEverythingItSaid()
    {
        NewHistory().Add(Alert("Contoso Notes", minute: 20, acknowledged: true));

        var kept = Assert.Single(NewHistory().Load().Alerts);

        Assert.Equal(("Contoso Notes", "Contoso", @"C:\Program Files\Contoso", new DateTime(2026, 10, 5, 15, 20, 0), "Registry", true),
            (kept.Name, kept.Publisher, kept.InstallPath, kept.DetectedAt, kept.Source, kept.IsAcknowledged));
    }

    [Fact]
    public void Detections_AreKeptNewestFirst()
    {
        var history = NewHistory();
        history.Add(Alert("First", minute: 1));
        history.Add(Alert("Second", minute: 2));

        Assert.Equal(["Second", "First"], NewHistory().Load().Alerts.Select(a => a.Name));
    }

    [Fact]
    public void AtMostMaxAlertsAreKept_TheNewest()
    {
        var history = NewHistory();
        for (var i = 0; i <= AppAlertHistory.MaxAlerts; i++)
            history.Add(Alert($"App {i:D3}"));

        var kept = NewHistory().Load().Alerts;

        Assert.Equal(AppAlertHistory.MaxAlerts, kept.Count);
        Assert.Equal($"App {AppAlertHistory.MaxAlerts:D3}", kept[0].Name);
        Assert.DoesNotContain(kept, a => a.Name == "App 000");
    }

    [Fact]
    public void AcknowledgeAll_IsKept()
    {
        var history = NewHistory();
        history.Add(Alert("First"));
        history.Add(Alert("Second"));

        history.AcknowledgeAll();

        Assert.All(NewHistory().Load().Alerts, a => Assert.True(a.IsAcknowledged));
    }

    [Fact]
    public void AcknowledgeAll_WithNothingNew_WritesNothing()
    {
        NewHistory().Add(Alert("First", acknowledged: true));
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(StoreFile, stamp);

        NewHistory().AcknowledgeAll();

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(StoreFile));
    }

    [Fact]
    public void Clear_RemovesTheRecord()
    {
        NewHistory().Add(Alert("First"));

        Assert.True(NewHistory().Clear());

        Assert.False(File.Exists(StoreFile));
        Assert.Empty(NewHistory().Load().Alerts);
    }

    [Fact]
    public void Clear_WithNothingKept_Succeeds()
    {
        Assert.True(NewHistory().Clear());
    }

    [Fact]
    public void Clear_WhenTheRecordCannotBeRemoved_SaysSo()
    {
        NewHistory().Add(Alert("First"));

        bool cleared;
        using (new FileStream(StoreFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            cleared = NewHistory().Clear();

        Assert.False(cleared);
        Assert.Single(NewHistory().Load().Alerts);
    }

    [Fact]
    public void WhenTheRecordCannotBeRead_ItSaysSo_AndADetectionWritesNothingOverIt()
    {
        NewHistory().Add(Alert("First"));
        var before = File.ReadAllBytes(StoreFile);

        AppAlertsRead read;
        using (new FileStream(StoreFile, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            read = NewHistory().Load();
            NewHistory().Add(Alert("Second"));
            NewHistory().AcknowledgeAll();
        }

        Assert.Same(AppAlertsRead.Unreadable, read);
        Assert.Equal(before, File.ReadAllBytes(StoreFile));
    }

    [Fact]
    public void ARecordThatDoesNotParse_IsSetAside_AndTheNextDetectionStartsANewOne()
    {
        File.WriteAllText(StoreFile, "{ not json");

        NewHistory().Add(Alert("First"));

        Assert.Equal("{ not json", File.ReadAllText(StoreFile + ".unreadable"));
        Assert.Equal("First", Assert.Single(NewHistory().Load().Alerts).Name);
    }

    [Fact]
    public void ARecordThatDoesNotParse_AndCannotBeSetAside_IsLeftAsItIs()
    {
        File.WriteAllText(StoreFile, "{ not json");
        // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not.
        Directory.CreateDirectory(StoreFile + ".unreadable");

        Assert.Same(AppAlertsRead.Unreadable, NewHistory().Load());
        NewHistory().Add(Alert("First"));
        Assert.Equal("{ not json", File.ReadAllText(StoreFile));
    }

    [Fact]
    public void Parse_FillsWhatTheFileLeftOut_AndDropsNamelessEntries()
    {
        var json = JsonSerializer.Serialize(new List<AppAlertHistory.StoredAlert>
        {
            new("Contoso Notes", null!, null!, new DateTime(2026, 10, 5, 15, 0, 0), null!, false),
            new("", "Contoso", "", new DateTime(2026, 10, 5, 15, 0, 0), "Registry", false),
        });

        var alert = Assert.Single(AppAlertHistory.Parse(json)!);

        Assert.Equal(("Contoso Notes", "", "", ""), (alert.Name, alert.Publisher, alert.InstallPath, alert.Source));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ not json")]
    public void Parse_OfSomethingThatIsNotAList_IsNull(string json)
    {
        Assert.Null(AppAlertHistory.Parse(json));
    }

    [Fact]
    public void Add_RefusesNoDetection()
    {
        Assert.Throws<ArgumentNullException>(() => NewHistory().Add(null!));
    }
}
