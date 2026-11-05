// SysManager · ListItemWordsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// The words each list item type gives a screen reader through its <c>ToString</c>. A list that draws its items
/// through a template or a member path announces each item by that, and a record's own read out every field while a
/// class's was its type name (#2785). <c>ArchitectureTests.EveryListItem_IsAnnouncedByItsWords</c> holds every such
/// list to having words; these hold the words to what the list shows.
/// </summary>
public class ListItemWordsTests
{
    [Fact]
    public void ALiveOutputLine_IsItsText()
        => Assert.Equal(@"Scanning C:\Windows\Temp", PowerShellLine.Output(@"Scanning C:\Windows\Temp").ToString());

    [Fact]
    public void ADisplay_IsItsName_AndSaysWhenItIsTheMainOne()
    {
        Assert.Equal("DELL U2723QE (primary)",
            new DisplayDevice(@"\\.\DISPLAY1", "DELL U2723QE", IsPrimary: true, IsActive: true).ToString());
        Assert.Equal("LG 27GL850",
            new DisplayDevice(@"\\.\DISPLAY2", "LG 27GL850", IsPrimary: false, IsActive: true).ToString());
    }

    [Fact]
    public void AProcess_IsItsNameAndId()
        => Assert.Equal("doom.exe (4242)", new RunningProcess(4242, "doom.exe", AffinityMask: 0).ToString());

    [Fact]
    public void APickersChoice_IsItsLabel()
    {
        Assert.Equal("Gaming", new VolumePreset("Gaming", []).ToString());
        Assert.Equal("Headphones (Studio)",
            new AudioDevice("{headphones}", "Headphones (Studio)", IsDefault: true).ToString());
        Assert.Equal("Last 24 hours", new HistoryRange("Last 24 hours", TimeSpan.FromHours(24)).ToString());
        Assert.Equal("Global", TargetPresets.Global.ToString());
        Assert.Equal("Downloads", new ScanLocation("Downloads", @"C:\Users\Public\Downloads").ToString());
    }

    [Fact]
    public void AFolderRow_IsWhatTheRowShows_InTheRowsOwnFigures()
    {
        var folder = new DiskUsageEntry
        {
            Name = "AppData",
            SizeBytes = 40_802_189_312,
            FileCount = 1204,
            FolderCount = 61,
        };

        // German groups thousands with a dot, and the row draws "1,204 files" whatever the PC's region.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Assert.Equal($"AppData, {FormatHelper.FormatSize(40_802_189_312)}, 1,204 files, 61 folders",
                folder.ToString());
            folder.IsAccessDenied = true;
            Assert.EndsWith(", 61 folders, partly unreadable", folder.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void AGridRow_IsWhatItIsCalled_NotItsType()
    {
        // Every grid row was announced as its type, "SysManager.Models.InstalledApp" (#2785).
        Assert.Equal("7-Zip 24.08 (x64)", new InstalledApp { Name = "7-Zip 24.08 (x64)" }.ToString());
        Assert.Equal("chrome (1234)", new ProcessEntry { Name = "chrome", Pid = 1234 }.ToString());
        Assert.Equal("Hop 3, 10.0.0.1", new TracerouteHop { HopNumber = 3, Address = "10.0.0.1" }.ToString());
        Assert.Equal("Cookies in Chrome (Work)",
            new BrowserCleanupItem { Browser = "Chrome (Work)", Category = "Cookies", Description = "", Paths = [] }
                .ToString());
    }

    [Fact]
    public void ASpeedTestRow_IsItsTimeAndDownload_InTheGridsOwnFigures()
    {
        var result = new SpeedTestResult("HTTP", 95.24, 41.7, 12, "Cloudflare", new DateTime(2026, 11, 5, 18, 30, 0));

        // German writes "95,2" and "Nov.", and the grid draws "05 Nov 18:30" and "95.2" whatever the PC's region.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Assert.Equal("05 Nov 18:30, 95.2 Mbps down", result.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ADuplicateGroup_IsItsHeader()
        => Assert.Equal("3 files, 1,048,576 bytes each",
            new DuplicateFileGroup { Count = 3, FileSize = 1_048_576 }.ToString());
}
