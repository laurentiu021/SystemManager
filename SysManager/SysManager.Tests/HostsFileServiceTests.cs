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

    [Fact]
    public async Task ReadHostsAsync_MultipleHostnamesPerIp_AllPreserved()
    {
        // Regression (data loss): a line mapping one IP to several hostnames
        // ("127.0.0.1  a  b  c") previously kept only the first hostname, so the
        // others were dropped on a read→save round trip. Each must survive as its
        // own entry.
        var (svc, _, dir) = NewServiceWithTempHosts("127.0.0.1\talpha beta gamma\n");
        try
        {
            var entries = await svc.ReadHostsAsync();
            var hosts = entries.Where(e => e.IpAddress == "127.0.0.1").Select(e => e.Hostname).ToList();
            Assert.Contains("alpha", hosts);
            Assert.Contains("beta", hosts);
            Assert.Contains("gamma", hosts);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SaveHosts_PreservesOriginalFileIdentity_NotJustContent()
    {
        // Regression (ACL/attribute loss): SaveHosts must REPLACE the existing hosts
        // file in place (File.Replace) rather than relink a brand-new inode over it
        // (File.Move overwrite). A brand-new file would inherit the directory's default
        // security descriptor instead of the security-hardened hosts file's own DACL.
        // We can't assert the System32 DACL without admin, but File.Replace preserves the
        // replaced file's creation time whereas File.Move resets it to "now" — so a
        // preserved (old) creation time proves the in-place replace path is taken.
        var (svc, hosts, dir) = NewServiceWithTempHosts("127.0.0.1 localhost\n");
        try
        {
            var original = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetCreationTimeUtc(hosts, original);

            svc.SaveHosts(new List<HostsEntry>
            {
                new() { IpAddress = "10.0.0.1", Hostname = "managed", IsEnabled = true }
            });

            // File.Replace keeps the original creation timestamp; File.Move(overwrite)
            // would have stamped it with the moment the temp file was written.
            Assert.Equal(original, File.GetCreationTimeUtc(hosts));
            Assert.Contains("managed", File.ReadAllText(hosts));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RestoreBackup_PreservesOriginalFileIdentity_NotJustContent()
    {
        // Regression (idx 158, ACL/attribute loss): RestoreBackup must replace the hosts
        // file in place (File.Replace) so the hardened DACL is preserved, not relink a new
        // inode (File.Copy overwrite). Same creation-time proxy as the SaveHosts test:
        // File.Replace keeps the original creation time; a fresh copy would reset it.
        const string original = "# ORIGINAL pristine hosts\n127.0.0.1 original\n";
        var (svc, hosts, dir) = NewServiceWithTempHosts(original);
        try
        {
            var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetCreationTimeUtc(hosts, stamp);

            svc.SaveHosts(new List<HostsEntry>
            {
                new() { IpAddress = "9.9.9.9", Hostname = "managed", IsEnabled = true }
            });
            Assert.True(svc.RestoreBackup());

            Assert.Equal(original, File.ReadAllText(hosts));
            Assert.Equal(stamp, File.GetCreationTimeUtc(hosts));
            Assert.False(File.Exists(hosts + ".sysmanager.restore.tmp"), "restore temp file left behind");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---------- AddEntry validation (idx 156 + the missing negative tests) ----------

    [Theory]
    [InlineData("999.999.999.999")]   // out-of-range octets
    [InlineData("1.2.3")]             // too few octets
    [InlineData("notanip")]
    [InlineData("")]
    public void AddEntry_InvalidIp_Throws(string ip)
    {
        var (svc, _, dir) = NewServiceWithTempHosts("127.0.0.1 localhost\n");
        try
        {
            Assert.Throws<ArgumentException>(() => svc.AddEntry(ip, "example.com"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Theory]
    [InlineData("")]                  // empty
    [InlineData("   ")]               // whitespace
    [InlineData("bad host")]          // space
    [InlineData("a..b")]              // consecutive dots — accepted by the old loose regex
    [InlineData(".leadingdot")]       // leading dot
    [InlineData("trailingdot.")]      // trailing dot
    [InlineData("under_score")]       // underscore not allowed in DNS labels
    [InlineData("-leadinghyphen.com")]
    public void AddEntry_InvalidHostname_Throws(string hostname)
    {
        var (svc, _, dir) = NewServiceWithTempHosts("127.0.0.1 localhost\n");
        try
        {
            Assert.Throws<ArgumentException>(() => svc.AddEntry("1.1.1.1", hostname));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("sub.domain.example.com")]
    [InlineData("localhost")]
    [InlineData("a-b.example")]
    public void AddEntry_ValidInput_Succeeds(string hostname)
    {
        var (svc, _, dir) = NewServiceWithTempHosts("127.0.0.1 localhost\n");
        try
        {
            var entry = svc.AddEntry("1.1.1.1", hostname);
            Assert.Equal(hostname, entry.Hostname);
            Assert.Equal("1.1.1.1", entry.IpAddress);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void SaveHosts_LeavesNoTempFileBehind()
    {
        // Regression (atomic write): SaveHosts writes to a temp file then moves it
        // into place. After a successful save no ".sysmanager.tmp" must remain.
        var (svc, hosts, dir) = NewServiceWithTempHosts("127.0.0.1 localhost\n");
        try
        {
            svc.SaveHosts(new List<HostsEntry>
            {
                new() { IpAddress = "127.0.0.1", Hostname = "localhost", IsEnabled = true }
            });
            Assert.False(File.Exists(hosts + ".sysmanager.tmp"), "temp file was left behind after save");
            Assert.True(File.Exists(hosts));
            Assert.Contains("localhost", File.ReadAllText(hosts));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
