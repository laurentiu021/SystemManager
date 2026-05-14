// SysManager · DuplicateFileGroupTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DuplicateFileGroup"/> model — validates WastedBytes
/// calculation and property change notifications.
/// </summary>
public class DuplicateFileGroupTests
{
    [Fact]
    public void WastedBytes_CalculatesCorrectly()
    {
        var group = new DuplicateFileGroup();
        group.FileSize = 1024;
        group.Count = 3;
        // Wasted = (3 - 1) * 1024 = 2048
        Assert.Equal(2048, group.WastedBytes);
    }

    [Fact]
    public void WastedBytes_ZeroWhenCountIsOne()
    {
        var group = new DuplicateFileGroup();
        group.FileSize = 5000;
        group.Count = 1;
        Assert.Equal(0, group.WastedBytes);
    }

    [Fact]
    public void WastedBytes_ZeroWhenCountIsZero()
    {
        var group = new DuplicateFileGroup();
        group.FileSize = 5000;
        group.Count = 0;
        Assert.Equal(0, group.WastedBytes);
    }

    [Fact]
    public void WastedBytes_NotifiesWhenFileSizeChanges()
    {
        var group = new DuplicateFileGroup();
        group.Count = 3;
        var changed = new List<string>();
        group.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        group.FileSize = 2048;

        Assert.Contains("WastedBytes", changed);
    }

    [Fact]
    public void WastedBytes_NotifiesWhenCountChanges()
    {
        var group = new DuplicateFileGroup();
        group.FileSize = 1024;
        var changed = new List<string>();
        group.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        group.Count = 5;

        Assert.Contains("WastedBytes", changed);
    }

    [Fact]
    public void Hash_PropertyNotifies()
    {
        var group = new DuplicateFileGroup();
        var changed = new List<string>();
        group.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        group.Hash = "abc123";

        Assert.Contains("Hash", changed);
    }

    [Fact]
    public void Files_Collection_IsInitialized()
    {
        var group = new DuplicateFileGroup();
        Assert.NotNull(group.Files);
        Assert.Empty(group.Files);
    }

    // ── DuplicateFileEntry tests ──

    [Fact]
    public void DuplicateFileEntry_Properties_Notify()
    {
        var entry = new DuplicateFileEntry();
        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        entry.Path = @"C:\test\file.txt";
        entry.Name = "file.txt";
        entry.SizeBytes = 4096;
        entry.LastModified = DateTime.Now;
        entry.IsSelected = true;

        Assert.Contains("Path", changed);
        Assert.Contains("Name", changed);
        Assert.Contains("SizeBytes", changed);
        Assert.Contains("LastModified", changed);
        Assert.Contains("IsSelected", changed);
    }
}
