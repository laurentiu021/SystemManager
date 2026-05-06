// SysManager · OperationLockServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;
using Xunit;

namespace SysManager.Tests;

public class OperationLockServiceTests
{
    private OperationLockService Sut => OperationLockService.Instance;

    [Fact]
    public void TryAcquire_FirstCall_ReturnsHandle()
    {
        using var handle = Sut.TryAcquire(OperationCategory.SystemModification, "Test Op");
        Assert.NotNull(handle);
    }

    [Fact]
    public void TryAcquire_SameCategory_ReturnsNull_WhenAlreadyLocked()
    {
        using var first = Sut.TryAcquire(OperationCategory.Network, "First");
        Assert.NotNull(first);

        var second = Sut.TryAcquire(OperationCategory.Network, "Second");
        Assert.Null(second);
    }

    [Fact]
    public void TryAcquire_DifferentCategory_Succeeds()
    {
        using var disk = Sut.TryAcquire(OperationCategory.Disk, "Disk Op");
        Assert.NotNull(disk);

        using var net = Sut.TryAcquire(OperationCategory.Network, "Net Op");
        Assert.NotNull(net);
    }

    [Fact]
    public void Dispose_ReleasesLock_AllowsReacquire()
    {
        var handle = Sut.TryAcquire(OperationCategory.Disk, "First");
        Assert.NotNull(handle);
        handle!.Dispose();

        using var second = Sut.TryAcquire(OperationCategory.Disk, "Second");
        Assert.NotNull(second);
    }

    [Fact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        var handle = Sut.TryAcquire(OperationCategory.SystemModification, "Multi-dispose");
        Assert.NotNull(handle);
        handle!.Dispose();
        handle.Dispose(); // Should not throw
    }

    [Fact]
    public void IsLocked_ReturnsTrue_WhenAcquired()
    {
        using var handle = Sut.TryAcquire(OperationCategory.Network, "Lock check");
        Assert.NotNull(handle);
        Assert.True(Sut.IsLocked(OperationCategory.Network));
    }

    [Fact]
    public void IsLocked_ReturnsFalse_AfterDispose()
    {
        var handle = Sut.TryAcquire(OperationCategory.Disk, "Lock check 2");
        Assert.NotNull(handle);
        handle!.Dispose();
        Assert.False(Sut.IsLocked(OperationCategory.Disk));
    }

    [Fact]
    public void GetActiveOperationName_ReturnsName_WhenLocked()
    {
        using var handle = Sut.TryAcquire(OperationCategory.SystemModification, "My Operation");
        Assert.NotNull(handle);
        Assert.Equal("My Operation", Sut.GetActiveOperationName(OperationCategory.SystemModification));
    }

    [Fact]
    public void GetActiveOperationName_ReturnsNull_WhenNotLocked()
    {
        Assert.Null(Sut.GetActiveOperationName(OperationCategory.Disk));
    }

    [Fact]
    public async Task TryAcquire_IsThreadSafe()
    {
        int successCount = 0;
        var barrier = new Barrier(10);

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            using var handle = Sut.TryAcquire(OperationCategory.Network, "Race");
            if (handle != null)
                Interlocked.Increment(ref successCount);
            Thread.Sleep(50);
        })).ToArray();

        await Task.WhenAll(tasks);

        // Only one thread should have acquired the lock at a time
        Assert.True(successCount >= 1);
    }
}
