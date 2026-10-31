// SysManager · DeepCleanupPreviousInstallationTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Deep Cleanup removes Windows.old through Windows' own cleanup for it, and never by deleting it file by file (#2720).
/// </summary>
/// <remarks>
/// Most of a previous installation is owned by TrustedInstaller, so the delete file by file was refused for most of it,
/// and what it did remove left a rollback that no longer works. The cleanup is a substitute, so no test here removes the
/// previous installation of the PC running it. Where a test needs it to have worked, the substitute deletes the planted
/// folder the way Windows would, which is how the freed total is shown to come from measuring the folder.
/// </remarks>
public sealed class DeepCleanupPreviousInstallationTests
{
    private const string BucketName = "Windows.old (previous Windows installation)";

    private static string WindowsOld(TempCleanupRoots roots) => Path.Combine(roots.SystemDrive, "Windows.old");

    /// <summary>A cleanup that does what <paramref name="run"/> says and ends with <paramref name="exitCode"/>.</summary>
    private static IPreviousInstallationCleanup Cleanup(Action run, int exitCode = 0)
    {
        var cleanup = Substitute.For<IPreviousInstallationCleanup>();
        cleanup.RemoveAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            run();
            return Task.FromResult(exitCode);
        });
        return cleanup;
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
    public async Task Scan_MarksWindowsOldAsWindowsOwnToRemove_AndNoOtherBucket()
    {
        using var roots = new TempCleanupRoots();
        TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Windows", "System32", "notepad.exe"), 10);

        var categories = await new DeepCleanupService(roots).ScanAsync();

        Assert.Equal(BucketName, Assert.Single(categories, c => c.IsPreviousWindowsInstallation).Name);
        Assert.Contains("administrator", Assert.Single(categories, c => c.IsPreviousWindowsInstallation).Description,
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_HandsWindowsOldToWindows_AndCountsWhatItFreed()
    {
        using var roots = new TempCleanupRoots();
        TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Windows", "System32", "notepad.exe"), 10);
        TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Users", "old", "notes.txt"), 5);
        // What Windows' cleanup does: the whole previous installation goes.
        var cleanup = Cleanup(() => Directory.Delete(WindowsOld(roots), recursive: true));
        var service = new DeepCleanupService(roots, previousInstallation: cleanup);
        var categories = await ScanAndTickAsync(service, BucketName);

        var result = await service.CleanAsync(categories);

        await cleanup.Received(1).RemoveAsync(Arg.Any<CancellationToken>());
        Assert.Equal(15, result.BytesFreed);
        Assert.Equal(2, result.FilesDeleted);
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// When Windows' cleanup removes nothing, nothing is removed: the bucket does not delete the files itself.
    /// </summary>
    [Fact]
    public async Task Clean_NeverDeletesThePreviousInstallationItself()
    {
        using var roots = new TempCleanupRoots();
        var file = TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Windows", "System32", "notepad.exe"), 10);
        var service = new DeepCleanupService(roots, previousInstallation: Cleanup(() => { }));
        var categories = await ScanAndTickAsync(service, BucketName);

        var result = await service.CleanAsync(categories);

        Assert.True(File.Exists(file), "the bucket deleted a file of the previous installation itself");
        Assert.Equal(0, result.BytesFreed);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal($"{BucketName}: Windows left 10 B of it in place.", Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(SecurityException))]
    public async Task Clean_WithoutAdministratorRights_SaysSoAndLeavesTheFiles(Type refusal)
    {
        using var roots = new TempCleanupRoots();
        var file = TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Windows", "System32", "notepad.exe"), 10);
        var cleanup = Substitute.For<IPreviousInstallationCleanup>();
        cleanup.RemoveAsync(Arg.Any<CancellationToken>()).ThrowsAsync((Exception)Activator.CreateInstance(refusal)!);
        var service = new DeepCleanupService(roots, previousInstallation: cleanup);
        var categories = await ScanAndTickAsync(service, BucketName);

        var result = await service.CleanAsync(categories);

        Assert.Equal($"{BucketName}: not removed. Windows removes a previous installation only for an administrator: "
                     + "use Run as administrator at the top of the page.", Assert.Single(result.Errors));
        Assert.True(File.Exists(file), "a refused cleanup must not fall back to deleting the files");
        Assert.Equal(0, result.BytesFreed);
    }

    [Fact]
    public async Task Clean_OnAWindowsWithoutThatCleanup_SaysSoAndLeavesTheFiles()
    {
        using var roots = new TempCleanupRoots();
        var file = TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Windows", "System32", "notepad.exe"), 10);
        var cleanup = Substitute.For<IPreviousInstallationCleanup>();
        cleanup.RemoveAsync(Arg.Any<CancellationToken>())
               .ThrowsAsync(new InvalidOperationException("Windows' Disk Cleanup is not on this PC."));
        var service = new DeepCleanupService(roots, previousInstallation: cleanup);
        var categories = await ScanAndTickAsync(service, BucketName);

        var result = await service.CleanAsync(categories);

        Assert.Equal($"{BucketName}: not removed. Windows' Disk Cleanup is not on this PC.", Assert.Single(result.Errors));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Clean_WhenDiskCleanupCannotStart_SaysSoAndLeavesTheFiles()
    {
        // An edition of Windows without cleanmgr.exe: the start fails, and the clean goes on.
        using var roots = new TempCleanupRoots();
        var file = TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Windows", "System32", "notepad.exe"), 10);
        var cleanup = Substitute.For<IPreviousInstallationCleanup>();
        cleanup.RemoveAsync(Arg.Any<CancellationToken>())
               .ThrowsAsync(new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified."));
        var service = new DeepCleanupService(roots, previousInstallation: cleanup);
        var categories = await ScanAndTickAsync(service, BucketName);

        var result = await service.CleanAsync(categories);

        Assert.Equal($"{BucketName}: not removed, because Windows' Disk Cleanup could not be started: "
                     + "The system cannot find the file specified.", Assert.Single(result.Errors));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Clean_WithoutTheCleanup_SaysSoAndLeavesTheFiles()
    {
        using var roots = new TempCleanupRoots();
        var file = TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Windows", "System32", "notepad.exe"), 10);
        var service = new DeepCleanupService(roots);
        var categories = await ScanAndTickAsync(service, BucketName);

        var result = await service.CleanAsync(categories);

        Assert.StartsWith($"{BucketName}: not removed", Assert.Single(result.Errors), StringComparison.Ordinal);
        Assert.True(File.Exists(file));
        Assert.Equal(0, result.BytesFreed);
    }

    [Fact]
    public async Task Clean_WhenDiskCleanupEndsWithAnError_SaysSo_AndWhatItLeft()
    {
        using var roots = new TempCleanupRoots();
        TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Windows", "System32", "notepad.exe"), 10);
        var service = new DeepCleanupService(roots, previousInstallation: Cleanup(() => { }, exitCode: 5));
        var categories = await ScanAndTickAsync(service, BucketName);

        var result = await service.CleanAsync(categories);

        Assert.Equal(
            [$"{BucketName}: Windows' Disk Cleanup stopped with code 5.", $"{BucketName}: Windows left 10 B of it in place."],
            result.Errors);
    }

    /// <summary>
    /// Windows' cleanup is not stopped part-way: that would leave the part-removed installation it is there to avoid.
    /// </summary>
    [Fact]
    public async Task Clean_DoesNotHandTheCancellationToWindowsCleanup()
    {
        using var roots = new TempCleanupRoots();
        TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Windows", "System32", "notepad.exe"), 10);
        var cleanup = Cleanup(() => Directory.Delete(WindowsOld(roots), recursive: true));
        var service = new DeepCleanupService(roots, previousInstallation: cleanup);
        var categories = await ScanAndTickAsync(service, BucketName);
        using var cts = new CancellationTokenSource();

        await service.CleanAsync(categories, ct: cts.Token);

        await cleanup.Received(1).RemoveAsync(Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    /// <summary>
    /// A clean cancelled while Windows' cleanup runs lets it finish, and returns what it removed, marked cancelled
    /// (#2736).
    /// </summary>
    /// <remarks>
    /// Measured to the end after the cancel: a walk cut short would have counted what Windows left as freed.
    /// </remarks>
    [Fact]
    public async Task Clean_CancelledWhileWindowsCleanupRuns_ReturnsWhatItRemoved()
    {
        using var roots = new TempCleanupRoots();
        TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Windows", "System32", "notepad.exe"), 10);
        TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Users", "left.dat"), 4);
        using var cts = new CancellationTokenSource();
        var cleanup = Cleanup(() =>
        {
            Directory.Delete(Path.Combine(WindowsOld(roots), "Windows"), recursive: true);
            cts.Cancel();
        });
        var service = new DeepCleanupService(roots, previousInstallation: cleanup);
        var categories = await ScanAndTickAsync(service, BucketName);

        var result = await service.CleanAsync(categories, ct: cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal((10L, 1), (result.BytesFreed, result.FilesDeleted));
        Assert.Contains(result.Errors, e => e.EndsWith("Windows left 4 B of it in place.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Only this bucket goes through Windows' cleanup: a bucket cleaned beside it still deletes its own files.
    /// </summary>
    [Fact]
    public async Task Clean_UsesWindowsCleanupForThisBucketOnly()
    {
        using var roots = new TempCleanupRoots();
        var file = TempCleanupRoots.WriteFile(Path.Combine(WindowsOld(roots), "Windows", "System32", "notepad.exe"), 10);
        var temp = TempCleanupRoots.WriteFile(Path.Combine(roots.UserTemp, "leftover.tmp"), 7);
        // Older than the week the temporary-files bucket leaves alone (#2890).
        File.SetCreationTimeUtc(temp, DateTime.UtcNow - TimeSpan.FromDays(30));
        File.SetLastWriteTimeUtc(temp, DateTime.UtcNow - TimeSpan.FromDays(30));
        var cleanup = Cleanup(() => { });
        var service = new DeepCleanupService(roots, previousInstallation: cleanup);
        var categories = await ScanAndTickAsync(service, BucketName, "Temporary files");

        var result = await service.CleanAsync(categories);

        Assert.False(File.Exists(temp), "the temporary-files bucket did not delete its own file");
        Assert.True(File.Exists(file));
        Assert.Equal(7, result.BytesFreed);
        await cleanup.Received(1).RemoveAsync(Arg.Any<CancellationToken>());
    }
}
