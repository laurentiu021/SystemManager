// SysManager · HostsFileServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Backup / restore tests for <see cref="HostsFileService"/>. Uses the path-injection
/// constructor so the real System32 hosts file is never touched and no admin is needed.
/// </summary>
public class HostsFileServiceTests
{
    private static (HostsFileService svc, string hosts, string dir) NewServiceWithTempHosts(string initialContent)
    {
        var dir = Path.Combine(Path.GetTempPath(), "smtest_hosts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var hosts = Path.Combine(dir, "hosts");
        File.WriteAllText(hosts, initialContent);
        return (new HostsFileService(hosts), hosts, dir);
    }

    [Fact]
    public void SaveHosts_PreservesPristineOriginal_AcrossMultipleSaves()
    {
        var (svc, hosts, dir) = NewServiceWithTempHosts("# ORIGINAL pristine hosts\n127.0.0.1 original\n");
        var backup = hosts + ".bak";
        try
        {
            // First save backs up the pristine original.
            svc.SaveHosts(new List<HostsEntry>
            {
                new() { IpAddress = "1.1.1.1", Hostname = "first", IsEnabled = true }
            });
            Assert.True(File.Exists(backup));
            var backupAfterFirst = File.ReadAllText(backup);
            Assert.Contains("ORIGINAL pristine hosts", backupAfterFirst);

            // Second save must NOT overwrite the backup with SysManager's own output.
            svc.SaveHosts(new List<HostsEntry>
            {
                new() { IpAddress = "2.2.2.2", Hostname = "second", IsEnabled = true }
            });
            var backupAfterSecond = File.ReadAllText(backup);
            Assert.Equal(backupAfterFirst, backupAfterSecond);
            Assert.Contains("ORIGINAL pristine hosts", backupAfterSecond);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RestoreBackup_RestoresPristineOriginal()
    {
        const string original = "# ORIGINAL pristine hosts\n127.0.0.1 original\n";
        var (svc, hosts, dir) = NewServiceWithTempHosts(original);
        try
        {
            svc.SaveHosts(new List<HostsEntry>
            {
                new() { IpAddress = "9.9.9.9", Hostname = "managed", IsEnabled = true }
            });
            Assert.DoesNotContain("ORIGINAL pristine hosts", File.ReadAllText(hosts));

            Assert.True(svc.HasBackup);
            Assert.True(svc.RestoreBackup());

            // After restore the file content matches the pristine original byte-for-byte.
            Assert.Equal(original, File.ReadAllText(hosts));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RestoreBackup_NoBackup_ReturnsFalse()
    {
        var (svc, _, dir) = NewServiceWithTempHosts("127.0.0.1 localhost\n");
        try
        {
            Assert.False(svc.HasBackup);
            Assert.False(svc.RestoreBackup());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
