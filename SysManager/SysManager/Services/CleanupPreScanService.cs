// SysManager · CleanupPreScanService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using SysManager.Helpers;

namespace SysManager.Services;

/// <inheritdoc cref="ICleanupPreScanService"/>
public sealed class CleanupPreScanService : ICleanupPreScanService
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
    // Both walks go through TuneUpService's walker rather than SearchOption.AllDirectories, which was wrong
    // three ways at once. AllDirectories throws UnauthorizedAccessException out of MoveNext(), and the catch
    // has to sit outside the foreach — so one protected subfolder ended the whole walk and the headline
    // reported whatever had been summed up to that point, silently low. It also follows junctions, so bytes
    // living outside the tree counted as freeable; in the Recycle Bin that is the common case, because
    // deleting a junction puts a reparse point IN the bin. And the temp figure has to exclude what the sweep
    // refuses to delete, or it promises space Clean TEMP will correctly leave alone.
    private string MeasureTemp()
    {
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("TEMP") ?? "",
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
        };

        // The same two exclusions CleanTempFilesAsync passes, for the same reason: this number describes
        // what that method would free, and it skips both extraction roots.
        var bytes = paths
            .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
            .Sum(path => SumFileLengths(TuneUpService.EnumerateFilesSkippingReparsePoints(
                path, CancellationToken.None,
                SystemPaths.BundleExtractionRoot, SystemPaths.OwnExtractionDirectory)));

        return Describe(bytes, "can be freed");
    }

    private string MeasureRecycleBin()
    {
        try
        {
            long bytes = 0;
            // The Recycle Bin lives in a hidden $Recycle.Bin folder on EVERY fixed drive, one per-SID
            // subfolder per user. Empty (SHEmptyRecycleBin) only clears the CURRENT user's bin, so size only
            // THIS user's per-SID folders — summing the whole tree over-reports what emptying can actually
            // free on a multi-user box.
            foreach (var path in RecycleBinHelper.CurrentUserBinPaths())
            {
                if (!Directory.Exists(path)) continue;
                bytes += SumFileLengths(TuneUpService.EnumerateFilesSkippingReparsePoints(
                    path, CancellationToken.None,
                    SystemPaths.BundleExtractionRoot, SystemPaths.OwnExtractionDirectory));
            }

            return Describe(bytes, "in Recycle Bin");
        }
        catch (IOException) { return "Unable to scan"; }
        catch (UnauthorizedAccessException) { return "Unable to scan"; }
    }

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

    private static string Describe(long bytes, string suffix) =>
        bytes > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:F1} MB {suffix}")
            : "Empty";
}
