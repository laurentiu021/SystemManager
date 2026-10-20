// SysManager · CleanupPreScanService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using SysManager.Helpers;

namespace SysManager.Services;

/// <inheritdoc cref="ICleanupPreScanService"/>
/// <param name="recycleBin">Where the Recycle Bin's figure comes from: the shell's own count of it.</param>
public sealed class CleanupPreScanService(IRecycleBin recycleBin) : ICleanupPreScanService
{
    /// <inheritdoc/>
    public Task<CleanupPreScan> MeasureAsync() =>
        Task.Run(() => new CleanupPreScan(MeasureTemp(), MeasureRecycleBin()));

    // The two measure methods are instance methods deliberately, and must stay that way.
    // ArchitectureTests.StaticPathMethods_AcceptARedirect reflects over SysManager.Services and INVOKES every
    // parameterless static method that returns a string, to see whether it resolves a path under the user's
    // profile with no override. As statics these two were called for real, walking the entire temp tree and
    // every per-SID Recycle Bin folder, and that guard sat there for twenty minutes. They return a size LABEL
    // rather than a path, so it was not even reporting anything — pure collateral cost.
    // The temp walk goes through SafeFileWalk rather than SearchOption.AllDirectories, which was wrong three
    // ways at once. AllDirectories throws UnauthorizedAccessException out of MoveNext(), and the catch has to
    // sit outside the foreach — so one protected subfolder ended the whole walk and the headline reported
    // whatever had been summed up to that point, silently low. It also follows junctions, so bytes living
    // outside the tree counted as freeable. And the temp figure has to exclude what the sweep refuses to
    // delete, or it promises space Clean TEMP will correctly leave alone.
    private string MeasureTemp()
    {
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("TEMP") ?? "",
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
        };

        // TuneUpService.TempWalk, not a second copy of its exclusions: this number claims to describe what
        // CleanTempFilesAsync would free, which is only true while both walk the same set.
        var bytes = paths
            .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
            .Sum(path => SumFileLengths(
                SafeFileWalk.Files(path, CancellationToken.None, TuneUpService.TempWalk)));

        return Describe(bytes, "can be freed");
    }

    /// <summary>The Recycle Bin's label, from the shell's count of the current user's bin on every drive.</summary>
    /// <remarks>
    /// The shell's count is what Empty Recycle Bin then empties, and like the empty it covers the current user's bin
    /// only. Walking the bin's folders counted the shell's own bookkeeping as well, the <c>desktop.ini</c> every bin
    /// folder holds, so an empty bin read "0.0 MB in Recycle Bin" rather than "Empty".
    /// </remarks>
    internal string MeasureRecycleBin() => recycleBin.Query() switch
    {
        null => "Unable to scan",
        { Items: 0 } => "Empty",
        { } bin => Megabytes(bin.Bytes, "in Recycle Bin"),
    };

    /// <summary>Total size of the given files, skipping any that cannot be read.</summary>
    private static long SumFileLengths(IEnumerable<string> files)
    {
        long bytes = 0;
        foreach (var file in files)
        {
            try { bytes += new FileInfo(file).Length; }
            catch (IOException) { /* skip inaccessible file */ }
            catch (UnauthorizedAccessException) { /* skip protected file */ }
        }

        return bytes;
    }

    private static string Describe(long bytes, string suffix) => bytes > 0 ? Megabytes(bytes, suffix) : "Empty";

    private static string Megabytes(long bytes, string suffix) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:F1} MB {suffix}");
}
