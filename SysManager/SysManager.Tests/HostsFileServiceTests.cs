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
        const string original = "# ORIGINAL pristine hosts\n127.0.0.1 originalhost\n";
        var (svc, hosts, dir) = NewServiceWithTempHosts(original);
        try
        {
            svc.SaveHosts(new List<HostsEntry>
            {
                new() { IpAddress = "9.9.9.9", Hostname = "managed", IsEnabled = true }
            });
            // The original IP MAPPING is replaced by the managed one. (The standalone comment is
            // now legitimately preserved by F40, so we assert on the mapping, not the comment.)
            Assert.DoesNotContain("originalhost", File.ReadAllText(hosts));
            Assert.Contains("managed", File.ReadAllText(hosts));

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
    [InlineData("256.1.1.1")]         // first octet out of range
    [InlineData("notanip")]
    [InlineData("")]
    // NOTE: do NOT use "1.2.3" here — IPAddress.TryParse accepts dotted shorthand
    // ("1.2.3" -> 1.2.0.3), so it is a VALID address, not a rejection case.
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

    // ---------- F40: standalone comment / blank-line preservation ----------

    [Fact]
    public async Task SaveHosts_PreservesStandaloneComments_ThroughReadEditSaveRoundTrip()
    {
        // Regression (F40): editing one entry through the UI rewrote the whole file from the
        // parsed entries only, silently deleting the user's hand-written comments. A read →
        // (edit) → save round trip must keep those comment lines.
        const string original =
            "# My custom block list\n" +
            "# Managed by hand — do not delete\n" +
            "0.0.0.0\tads.example.com\n";
        var (svc, hosts, dir) = NewServiceWithTempHosts(original);
        try
        {
            var entries = await svc.ReadHostsAsync();     // parses the one mapping
            svc.SaveHosts(entries);                       // save without changing anything

            var saved = File.ReadAllText(hosts);
            Assert.Contains("# My custom block list", saved);
            Assert.Contains("# Managed by hand — do not delete", saved);
            Assert.Contains("ads.example.com", saved);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task SaveHosts_IsFixedPoint_RepeatedSavesDoNotAccumulate()
    {
        // The service is a singleton doing a canonical whole-file rewrite: preserving comments
        // naively would re-capture SysManager's own header and blank lines every save, growing
        // the file without bound. Two consecutive save cycles must produce identical output.
        const string original =
            "# Section A\n" +
            "127.0.0.1\tlocalhost\n";
        var (svc, hosts, dir) = NewServiceWithTempHosts(original);
        try
        {
            var entries = await svc.ReadHostsAsync();
            svc.SaveHosts(entries);
            var afterFirst = File.ReadAllText(hosts);

            var entries2 = await svc.ReadHostsAsync();
            svc.SaveHosts(entries2);
            var afterSecond = File.ReadAllText(hosts);

            Assert.Equal(afterFirst, afterSecond);
            // And the managed header appears exactly once, not once per save.
            var headerCount = afterSecond.Split("# This file is managed by SysManager").Length - 1;
            Assert.Equal(1, headerCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void SaveHosts_NoComments_OutputUnchangedFromCanonicalForm()
    {
        // A file with no standalone comments must produce exactly the canonical header + entries
        // (byte-for-byte the previous behaviour) — the preservation logic adds nothing here.
        var (svc, hosts, dir) = NewServiceWithTempHosts("127.0.0.1\tlocalhost\n");
        try
        {
            svc.SaveHosts(new List<HostsEntry>
            {
                new() { IpAddress = "127.0.0.1", Hostname = "localhost", IsEnabled = true }
            });
            var expected =
                "# This file is managed by SysManager" + Environment.NewLine +
                "# Entries marked with # at the start are disabled" + Environment.NewLine +
                Environment.NewLine +
                "127.0.0.1\tlocalhost" + Environment.NewLine;
            Assert.Equal(expected, File.ReadAllText(hosts));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task SaveHosts_CommentedOutEntry_NotDuplicatedAsStandaloneComment()
    {
        // A disabled entry ("# 0.0.0.0 blocked") round-trips as a HostsEntry with IsEnabled=false,
        // so it must NOT also be re-emitted as a standalone comment line — that would duplicate it.
        const string original =
            "# a real note\n" +
            "# 0.0.0.0\tblocked.example.com\n";
        var (svc, hosts, dir) = NewServiceWithTempHosts(original);
        try
        {
            var entries = await svc.ReadHostsAsync();
            Assert.Contains(entries, e => e.Hostname == "blocked.example.com" && !e.IsEnabled);

            svc.SaveHosts(entries);
            var saved = File.ReadAllText(hosts);

            var blockedCount = saved.Split("blocked.example.com").Length - 1;
            Assert.Equal(1, blockedCount);                 // exactly one (the disabled entry)
            Assert.Contains("# a real note", saved);       // the genuine comment survives
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
