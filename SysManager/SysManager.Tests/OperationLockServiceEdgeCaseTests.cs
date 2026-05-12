// SysManager · OperationLockService concurrency and edge-case tests
using SysManager.Services;

namespace SysManager.Tests;

public class OperationLockServiceEdgeCaseTests
{
    private static OperationLockService Service => OperationLockService.Instance;

    [Fact]
    public void TryAcquire_SameCategory_ReturnNull()
    {
        using var handle = Service.TryAcquire(OperationCategory.Network, "Test1");
        Assert.NotNull(handle);

        var handle2 = Service.TryAcquire(OperationCategory.Network, "Test2");
        Assert.Null(handle2);
    }

    [Fact]
    public void TryAcquire_DifferentCategories_BothSucceed()
    {
        using var h1 = Service.TryAcquire(OperationCategory.Disk, "DiskOp");
        using var h2 = Service.TryAcquire(OperationCategory.Network, "NetOp");
        Assert.NotNull(h1);
        Assert.NotNull(h2);
    }

    [Fact]
    public void Dispose_Handle_ReleasesLock()
    {
        var h1 = Service.TryAcquire(OperationCategory.SystemModification, "Op1");
        Assert.NotNull(h1);
        h1.Dispose();

        var h2 = Service.TryAcquire(OperationCategory.SystemModification, "Op2");
        Assert.NotNull(h2);
        h2!.Dispose();
    }

    [Fact]
    public void Dispose_DoubleDispose_NoThrow()
    {
        var handle = Service.TryAcquire(OperationCategory.Disk, "DoubleDispose");
        Assert.NotNull(handle);
        handle.Dispose();
        handle.Dispose(); // should not throw
    }

    [Fact]
    public void IsLocked_WhenAcquired_ReturnsTrue()
    {
        using var handle = Service.TryAcquire(OperationCategory.Network, "LockCheck");
        Assert.NotNull(handle);
        Assert.True(Service.IsLocked(OperationCategory.Network));
    }

    [Fact]
    public void IsLocked_WhenReleased_ReturnsFalse()
    {
        var handle = Service.TryAcquire(OperationCategory.Disk, "Released");
        Assert.NotNull(handle);
        handle.Dispose();
        Assert.False(Service.IsLocked(OperationCategory.Disk));
    }

    [Fact]
    public void GetActiveOperationName_WhenLocked_ReturnsName()
    {
        using var handle = Service.TryAcquire(OperationCategory.SystemModification, "MyOp");
        Assert.NotNull(handle);
        Assert.Equal("MyOp", Service.GetActiveOperationName(OperationCategory.SystemModification));
    }

    [Fact]
    public void GetActiveOperationName_WhenFree_ReturnsNull()
    {
        // Ensure released
        var handle = Service.TryAcquire(OperationCategory.Disk, "TempOp");
        handle?.Dispose();
        Assert.Null(Service.GetActiveOperationName(OperationCategory.Disk));
    }

    [Fact]
    public void HasActiveOperations_NoLocks_ReturnsFalse()
    {
        // Release all possible locks first
        var h1 = Service.TryAcquire(OperationCategory.Disk, "temp");
        h1?.Dispose();
        var h2 = Service.TryAcquire(OperationCategory.Network, "temp");
        h2?.Dispose();
        var h3 = Service.TryAcquire(OperationCategory.SystemModification, "temp");
        h3?.Dispose();

        Assert.False(Service.HasActiveOperations);
    }

    [Fact]
    public void HasActiveOperations_WithLock_ReturnsTrue()
    {
        using var handle = Service.TryAcquire(OperationCategory.Network, "Active");
        Assert.NotNull(handle);
        Assert.True(Service.HasActiveOperations);
    }

    [Fact]
    public void ActiveOperations_ReturnsSnapshot()
    {
        using var handle = Service.TryAcquire(OperationCategory.Disk, "SnapshotOp");
        Assert.NotNull(handle);

        var ops = Service.ActiveOperations;
        Assert.Contains(ops, o => o.Category == OperationCategory.Disk && o.Info.Name == "SnapshotOp");
    }

    [Fact]
    public void PropertyChanged_FiredOnAcquire()
    {
        var changed = new List<string>();
        Service.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        var handle = Service.TryAcquire(OperationCategory.Disk, "PropChanged");
        Assert.NotNull(handle);
        handle.Dispose();

        Assert.Contains("ActiveOperations", changed);
        Assert.Contains("HasActiveOperations", changed);
    }

    [Fact]
    public async Task ConcurrentAcquire_OnlyOneSucceeds()
    {
        // Release any existing network lock
        var existing = Service.TryAcquire(OperationCategory.Network, "preClean");
        existing?.Dispose();

        int successCount = 0;
        var handles = new List<OperationLockService.OperationHandle?>();
        var lockObj = new object();

        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            var h = Service.TryAcquire(OperationCategory.Network, $"Concurrent-{i}");
            lock (lockObj) { handles.Add(h); }
            if (h != null) Interlocked.Increment(ref successCount);
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(1, successCount);

        foreach (var h in handles) h?.Dispose();
    }
}
