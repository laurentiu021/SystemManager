// SysManager · DeepCleanupDeliveryOptimizationTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Deep Cleanup empties the Delivery Optimization cache through Windows' own <c>Delete-DeliveryOptimizationCache</c>,
/// and never by deleting the service's files itself (#2602).
/// </summary>
/// <remarks>
/// The runner is a substitute, so no test here empties the cache of the PC running it. Where a test needs the command
/// to have worked, the substitute deletes the planted files the way the command would, which is how the freed total is
/// shown to come from measuring the folders rather than from the scan.
/// </remarks>
public sealed class DeepCleanupDeliveryOptimizationTests
{
    private const string BucketName = "Delivery Optimization cache";

    /// <summary>Where current Windows keeps the cache, under the test's own Windows folder.</summary>
    private static string CacheDir(TempCleanupRoots roots) => Path.Combine(roots.WindowsDirectory, "ServiceProfiles",
        "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache");

    /// <summary>A runner whose every script does what <paramref name="run"/> says and returns no rows.</summary>
    private static IPowerShellRunner Runner(Action run)
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(_ =>
          {
              run();
              return Task.FromResult(new Collection<PSObject>());
          });
        return ps;
    }

    /// <summary>The categories the scan found, with only those named in <paramref name="names"/> left ticked.</summary>
    private static async Task<IReadOnlyList<CleanupCategory>> ScanAndTickAsync(DeepCleanupService service,
                                                                               params string[] names)
    {
        var categories = await service.ScanAsync();
        foreach (var category in categories) category.IsSelected = names.Contains(category.Name);
        Assert.Equal(names.Length, categories.Count(c => c.IsSelected));   // a renamed bucket must fail loudly here
        return categories;
    }

    [Fact]
    public async Task Clean_RunsWindowsOwnCommand_AndCountsWhatItFreed()
    {
        using var roots = new TempCleanupRoots();
        TempCleanupRoots.WriteFile(Path.Combine(CacheDir(roots), "a", "piece.bin"), 10);
        TempCleanupRoots.WriteFile(Path.Combine(CacheDir(roots), "b", "piece.bin"), 5);
        // What the command does: the service's files go, the cache folder stays.
        var ps = Runner(() =>
        {
            Directory.Delete(CacheDir(roots), recursive: true);
            Directory.CreateDirectory(CacheDir(roots));
        });
        var service = new DeepCleanupService(roots, ps);
        var categories = await ScanAndTickAsync(service, BucketName);

        var result = await service.CleanAsync(categories);

        await ps.Received(1).RunAsync(Arg.Is(DeepCleanupService.EmptyDeliveryOptimizationCacheScript),
                                      Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
        Assert.Equal(15, result.BytesFreed);
        Assert.Equal(2, result.FilesDeleted);
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// The command is Windows' own, and it is run without a prompt.
    /// </summary>
    /// <remarks>
    /// Without <c>-Force</c> the command waits for a key press through <c>$Host.UI.RawUI.ReadKey()</c>, which a
    /// background runspace has no console for. With <c>-IncludePinnedFiles</c> it would also delete what a download
    /// in progress is holding.
    /// </remarks>
    [Fact]
    public void TheCommand_IsWindowsOwn_AndAsksNothing()
    {
        var words = DeepCleanupService.EmptyDeliveryOptimizationCacheScript.Split(' ');

        Assert.Equal("Delete-DeliveryOptimizationCache", words[0]);
        Assert.Contains("-Force", words);
        Assert.DoesNotContain("-IncludePinnedFiles", words);
    }

    /// <summary>
    /// When the command frees nothing, nothing is freed: the bucket does not delete the service's files itself.
    /// </summary>
    /// <remarks>
    /// The bucket used to delete them file by file, under the service that keeps them (#2602). A command that keeps
    /// pinned files, or finds nothing to remove, has to read as 0 rather than as the scan's total.
    /// </remarks>
    [Fact]
    public async Task Clean_NeverDeletesTheCacheFilesItself()
    {
        using var roots = new TempCleanupRoots();
        var piece = TempCleanupRoots.WriteFile(Path.Combine(CacheDir(roots), "piece.bin"), 10);
        var service = new DeepCleanupService(roots, Runner(() => { }));
        var categories = await ScanAndTickAsync(service, BucketName);

        var result = await service.CleanAsync(categories);

        Assert.True(File.Exists(piece), "the bucket deleted the cache's file itself");
        Assert.Equal(0, result.BytesFreed);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Clean_WhenWindowsRefuses_SaysSoAndLeavesTheFiles()
    {
        using var roots = new TempCleanupRoots();
        var piece = TempCleanupRoots.WriteFile(Path.Combine(CacheDir(roots), "piece.bin"), 10);
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(_ => Task.FromException<Collection<PSObject>>(new RuntimeException("Access is denied.")));
        var service = new DeepCleanupService(roots, ps);
        var categories = await ScanAndTickAsync(service, BucketName);

        var result = await service.CleanAsync(categories);

        Assert.Equal($"{BucketName}: Access is denied.", Assert.Single(result.Errors));
        Assert.True(File.Exists(piece), "a refused command must not fall back to deleting the files");
        Assert.Equal(0, result.BytesFreed);
    }

    [Fact]
    public async Task Clean_WithoutARunner_SaysSoAndLeavesTheFiles()
    {
        using var roots = new TempCleanupRoots();
        var piece = TempCleanupRoots.WriteFile(Path.Combine(CacheDir(roots), "piece.bin"), 10);
        var service = new DeepCleanupService(roots);
        var categories = await ScanAndTickAsync(service, BucketName);

        var result = await service.CleanAsync(categories);

        Assert.StartsWith($"{BucketName}: not emptied", Assert.Single(result.Errors), StringComparison.Ordinal);
        Assert.True(File.Exists(piece));
        Assert.Equal(0, result.BytesFreed);
    }

    /// <summary>
    /// A clean stopped while the command runs is reported as cancelled, like a clean stopped anywhere else.
    /// </summary>
    [Fact]
    public async Task Clean_StoppedDuringTheCommand_IsCancelled()
    {
        using var roots = new TempCleanupRoots();
        TempCleanupRoots.WriteFile(Path.Combine(CacheDir(roots), "piece.bin"), 10);
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(_ => Task.FromCanceled<Collection<PSObject>>(new CancellationToken(canceled: true)));
        var service = new DeepCleanupService(roots, ps);
        var categories = await ScanAndTickAsync(service, BucketName);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CleanAsync(categories));
    }

    /// <summary>
    /// Only this bucket goes through the command: a bucket cleaned beside it still deletes its own files.
    /// </summary>
    [Fact]
    public async Task Clean_UsesTheCommandForThisBucketOnly()
    {
        using var roots = new TempCleanupRoots();
        var piece = TempCleanupRoots.WriteFile(Path.Combine(CacheDir(roots), "piece.bin"), 10);
        var temp = TempCleanupRoots.WriteFile(Path.Combine(roots.UserTemp, "leftover.tmp"), 7);
        var ps = Runner(() => { });
        var service = new DeepCleanupService(roots, ps);
        var categories = await ScanAndTickAsync(service, BucketName, "Temporary files");

        var result = await service.CleanAsync(categories);

        Assert.False(File.Exists(temp), "the temporary-files bucket did not delete its own file");
        Assert.True(File.Exists(piece));
        Assert.Equal(7, result.BytesFreed);
        await ps.Received(1).RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(),
                                      Arg.Any<CancellationToken>());
    }
}
