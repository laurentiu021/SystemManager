// SysManager · LargeFileScannerProgressTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.IO;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Tests that <see cref="LargeFileScanner"/> reports progress at all on a scan that finishes quickly.
/// Here rather than in the unit project because progress only exists while a real walk is running.
/// </summary>
public class LargeFileScannerProgressTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "SysManagerTests", "large-progress-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Collects reports on the thread that raises them.
    /// </summary>
    /// <remarks>
    /// NOT <see cref="Progress{T}"/>, which is what production uses: it POSTS each callback to a captured
    /// synchronization context, so a report raised just before the scan returns can still be in flight when
    /// the assertions run — a test that passes on timing rather than on behaviour. Implementing
    /// <see cref="IProgress{T}"/> directly makes <c>Report</c> synchronous. Same reasoning as
    /// <see cref="DuplicateFileServiceTests"/>, which pins the identical defect in the other scanner.
    /// </remarks>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        public ConcurrentQueue<T> Reports { get; } = new();
        public void Report(T value) => Reports.Enqueue(value);
    }

    /// <summary>
    /// Files big enough to clear the scan's minimum size, some in the root and some below it.
    /// </summary>
    /// <remarks>
    /// The root deliberately holds files of its own. Progress is reported at the end of each DIRECTORY's
    /// file loop, and the first directory walked is the root — so a tree whose root is empty reports a real
    /// folder with a count of zero, which is honest but proves nothing about the counts. Three files across
    /// two levels makes the first report carry both.
    /// </remarks>
    private void SeedTree()
    {
        var nested = Path.Combine(_root, "videos");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(_root, "clip-one.bin"), new byte[64 * 1024]);
        File.WriteAllBytes(Path.Combine(_root, "clip-two.bin"), new byte[96 * 1024]);
        File.WriteAllBytes(Path.Combine(nested, "clip-three.bin"), new byte[48 * 1024]);
    }

    /// <summary>
    /// A scan short enough to finish inside the report throttle still reports its progress.
    /// </summary>
    /// <remarks>
    /// The throttle is "not more often than every 200 ms", but seeding its timestamp with the current tick
    /// made it "not before 200 ms have passed" as well. The report also sits at the end of each DIRECTORY's
    /// file loop, so on a shallow tree there are only a handful of opportunities and all of them can fall
    /// inside that first window — leaving Deep Cleanup's Large Files panel showing 0 files, 0 bytes and a
    /// blank folder line for the whole scan (#2273). This tree is deliberately tiny, which is what makes it
    /// the case that used to report nothing.
    /// <para>Asserted on the first report's CONTENT as well as its existence: a report naming no folder and
    /// counting no files would satisfy "something was reported" while telling the user nothing.</para>
    /// </remarks>
    [Fact]
    public async Task ScanAsync_OnATreeFasterThanTheThrottle_StillReportsProgress()
    {
        SeedTree();
        var progress = new SynchronousProgress<LargeFileScanner.LargeFileProgress>();

        var results = await new LargeFileScanner().ScanAsync(
            _root, minSizeBytes: 1024, top: 10, progress: progress);

        Assert.Equal(3, results.Count);   // else the scan itself did not work and the rest proves nothing

        var reports = progress.Reports.ToList();

        // The FIRST report has to name a real folder inside the tree. Before the fix the only report on a
        // tree this size was the final one, whose folder was the literal string "Done" — so this assertion
        // failed on "Done" rather than on there being no report at all, which is how the second half of
        // #2273 was found.
        var first = reports.FirstOrDefault();
        Assert.NotNull(first);
        Assert.StartsWith(_root, first.CurrentFolder, StringComparison.OrdinalIgnoreCase);
        Assert.True(first.FilesScanned > 0,
            $"the first progress report counted {first.FilesScanned} files, so the panel it feeds would "
            + "show a folder name beside a count of zero.");

        // The LAST report exists to settle the counts and must name no folder, because the consumer renders
        // CurrentFolder verbatim into a line that says which folder is being scanned.
        var last = reports[^1];
        Assert.Equal(3, last.FilesScanned);
        Assert.True(string.IsNullOrEmpty(last.CurrentFolder),
            $"the final report named \"{last.CurrentFolder}\" as the folder being scanned. Nothing is being "
            + "scanned by then, and the panel shows that value as a folder name.");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp tree left behind is not a test failure */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
        GC.SuppressFinalize(this);
    }
}
