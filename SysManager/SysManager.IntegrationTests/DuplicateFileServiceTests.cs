// SysManager · DuplicateFileServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Tests for the shape of <see cref="DuplicateFileService"/>'s progress reports, against a real folder tree
/// on disk. Here rather than in the unit project because the reports only exist while a real walk is running:
/// nothing about them can be observed without a filesystem.
/// </summary>
public class DuplicateFileServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "SysManagerTests", "dupe-progress-" + Guid.NewGuid().ToString("N"));

    /// <summary>Two identical files in a subfolder, so the walk descends and finds a duplicate pair.</summary>
    private string SeedTree()
    {
        var nested = Path.Combine(_root, "photos", "2019");
        Directory.CreateDirectory(nested);
        var content = new byte[8 * 1024];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(Path.Combine(nested, "holiday.bin"), content);
        File.WriteAllBytes(Path.Combine(nested, "holiday-copy.bin"), content);
        return nested;
    }

    private static bool IsPerFile(DuplicateFileService.ScanProgress r) =>
        !string.Equals(r.Phase, DuplicateFileService.CompletePhase, StringComparison.Ordinal);

    /// <summary>
    /// Every per-file report carries the file's full path, not its name.
    /// </summary>
    /// <remarks>
    /// The consumer shows the leaf on its status row and the whole path on hover. While this reported
    /// <c>FileInfo.Name</c>, the hover was a copy of the row it was attached to — the same words, so pointing
    /// at the row told the user nothing (#2262). The same file name occurs in many folders, and which folder
    /// the scan is in is the one thing a single row cannot show.
    /// <para>Asserted as "a rooted path under the folder we asked about", not merely "contains a separator":
    /// a name with a separator in it would satisfy the weaker form.</para>
    /// </remarks>
    [Fact]
    public async Task ScanAsync_ReportsTheFullPathOfEachFile_NotJustItsName()
    {
        var nested = SeedTree();
        var progress = new SynchronousProgress<DuplicateFileService.ScanProgress>();

        await new DuplicateFileService().ScanAsync(_root, minSizeBytes: 1, progress);

        // The final report is a placeholder rather than a file, excluded by its named phase for the same
        // reason the view model skips it.
        var perFile = progress.Reports.Where(IsPerFile).ToList();

        Assert.NotEmpty(perFile);   // else everything below asserts over nothing

        // BOTH report sites have to be exercised, or this proves the shape of one and says nothing about the
        // other — which is exactly what happened: with the hashing pass unreported on a folder this small,
        // putting the bare name back at that site left the test green. The two phases are the two sites.
        Assert.Contains(perFile, r => r.Phase.StartsWith("Discovering", StringComparison.Ordinal));
        Assert.Contains(perFile, r => r.Phase.StartsWith("Hashing", StringComparison.Ordinal));

        foreach (var r in perFile)
        {
            Assert.True(Path.IsPathRooted(r.CurrentFile),
                $"a progress report named \"{r.CurrentFile}\", which is not a rooted path. The consumer puts "
                + "this on a tooltip beside a row that already shows the file name, so a bare name makes the "
                + "tooltip a copy of the row.");
            Assert.StartsWith(nested, r.CurrentFile, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(Path.GetFileName(r.CurrentFile), r.CurrentFile);
        }
    }

    /// <summary>
    /// A scan short enough to finish inside the report throttle still reports a file from its FIRST phase.
    /// </summary>
    /// <remarks>
    /// The throttle is "not more often than every 200 ms". Seeding its timestamp with the current tick made
    /// it "not before 200 ms have passed" as well, so a folder that scans faster than that produced no
    /// discovery report at all and the readout stayed blank until hashing began. This tree is deliberately
    /// tiny, which is what makes it the case that used to fail.
    /// <para>Asserted on the first report's PHASE rather than on "some report exists": the test above already
    /// requires reports from both phases, so a mere existence check here would duplicate it and pin nothing
    /// of its own. What only this test can see is whether the very first one had to wait.</para>
    /// </remarks>
    [Fact]
    public async Task ScanAsync_OnATreeFasterThanTheThrottle_StillReportsItsFirstDiscoveredFile()
    {
        SeedTree();
        var progress = new SynchronousProgress<DuplicateFileService.ScanProgress>();

        await new DuplicateFileService().ScanAsync(_root, minSizeBytes: 1, progress);

        var first = progress.Reports.FirstOrDefault(IsPerFile);
        Assert.NotNull(first);
        Assert.StartsWith("Discovering", first.Phase, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp tree left behind is not a test failure */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
        GC.SuppressFinalize(this);
    }
}
