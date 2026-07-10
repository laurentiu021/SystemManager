// SysManager · EnvironmentVariableServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="EnvironmentVariableService"/>. The pure helpers (name validation,
/// PATH split/join/dedup) and the file-backed backup/restore are exercised deterministically;
/// the live registry read/write path is intentionally not unit-tested (it touches the machine
/// environment and needs admin for the System scope).
/// </summary>
public class EnvironmentVariableServiceTests
{
    // ---------- ValidateName ----------

    [Theory]
    [InlineData("PATH")]
    [InlineData("JAVA_HOME")]
    [InlineData("_underscore")]
    [InlineData("My.Var")]
    [InlineData("Var-1")]
    public void ValidateName_AcceptsValidNames(string name)
        => Assert.Equal(name, EnvironmentVariableService.ValidateName(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has=equals")]
    [InlineData("1startsWithDigit")]
    [InlineData("=hiddenDriveVar")]
    [InlineData("semi;colon")]
    [InlineData("per%cent")]
    public void ValidateName_RejectsInvalidNames(string name)
        => Assert.Throws<ArgumentException>(() => EnvironmentVariableService.ValidateName(name));

    [Fact]
    public void ValidateName_RejectsOverlongName()
        => Assert.Throws<ArgumentException>(() => EnvironmentVariableService.ValidateName(new string('A', 256)));

    // ---------- ChooseKind (REG_EXPAND_SZ preservation) ----------

    [Fact]
    public void ChooseKind_PreservesExistingExpandString()
    {
        // The core regression: an existing PATH stored as REG_EXPAND_SZ must STAY expandable
        // even when its new value no longer literally contains a '%' at edit time, so its
        // %VAR% tokens keep expanding system-wide.
        Assert.Equal(RegistryValueKind.ExpandString,
            EnvironmentVariableService.ChooseKind(RegistryValueKind.ExpandString, @"C:\Tools;C:\Bin"));
    }

    [Fact]
    public void ChooseKind_PreservesExistingString()
    {
        Assert.Equal(RegistryValueKind.String,
            EnvironmentVariableService.ChooseKind(RegistryValueKind.String, @"%SystemRoot%\x"));
    }

    [Theory]
    [InlineData(@"%SystemRoot%\System32", true)]   // new value with a token → expandable
    [InlineData(@"C:\Plain\Path", false)]          // new value without a token → plain
    public void ChooseKind_NewVariable_UsesExpandStringWhenTokenPresent(string value, bool expectExpand)
    {
        var expected = expectExpand ? RegistryValueKind.ExpandString : RegistryValueKind.String;
        Assert.Equal(expected, EnvironmentVariableService.ChooseKind(null, value));
    }

    // ---------- SplitPath / JoinPath ----------

    [Fact]
    public void SplitPath_TrimsAndDropsEmptySegments()
    {
        var result = EnvironmentVariableService.SplitPath(@"C:\a ; ;C:\b;");
        Assert.Equal([@"C:\a", @"C:\b"], result);
    }

    [Fact]
    public void SplitPath_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(EnvironmentVariableService.SplitPath(null));
        Assert.Empty(EnvironmentVariableService.SplitPath(""));
    }

    [Fact]
    public void JoinPath_JoinsWithSemicolons_AndDropsBlanks()
    {
        var result = EnvironmentVariableService.JoinPath([@"C:\a", "  ", @"C:\b"]);
        Assert.Equal(@"C:\a;C:\b", result);
    }

    [Fact]
    public void SplitThenJoin_RoundTrips()
    {
        const string path = @"C:\Windows;C:\Windows\System32;C:\Tools";
        Assert.Equal(path, EnvironmentVariableService.JoinPath(EnvironmentVariableService.SplitPath(path)));
    }

    // ---------- Deduplicate ----------

    [Fact]
    public void Deduplicate_RemovesCaseInsensitiveDuplicates_KeepsFirst()
    {
        var result = EnvironmentVariableService.Deduplicate([@"C:\A", @"c:\a", @"C:\B"]);
        Assert.Equal([@"C:\A", @"C:\B"], result);
    }

    [Fact]
    public void Deduplicate_IgnoresTrailingSlashDifference()
    {
        var result = EnvironmentVariableService.Deduplicate([@"C:\A\", @"C:\A", @"C:\A\\"]);
        Assert.Single(result);
        Assert.Equal(@"C:\A\", result[0]);
    }

    [Fact]
    public void Deduplicate_NoDuplicates_PreservesAll()
    {
        var input = new[] { @"C:\A", @"C:\B", @"C:\C" };
        Assert.Equal(input, EnvironmentVariableService.Deduplicate(input));
    }

    // ---------- Backup / restore ----------

    private static (EnvironmentVariableService svc, string dir) NewServiceWithTempBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerEnvTest_" + Guid.NewGuid().ToString("N"));
        return (new EnvironmentVariableService(dir), dir);
    }

    [Fact]
    public void HasBackup_FalseBeforeEnsure_TrueAfter()
    {
        var (svc, dir) = NewServiceWithTempBackup();
        try
        {
            Assert.False(svc.HasBackup);
            svc.EnsureBackup();
            Assert.True(svc.HasBackup);
            Assert.True(File.Exists(svc.BackupPath));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EnsureBackup_DoesNotOverwriteExistingBackup()
    {
        var (svc, dir) = NewServiceWithTempBackup();
        try
        {
            svc.EnsureBackup();
            var firstWrite = File.GetLastWriteTimeUtc(svc.BackupPath);
            File.WriteAllText(svc.BackupPath, "{\"User\":{\"SENTINEL\":\"keep\"},\"Machine\":{}}");

            svc.EnsureBackup(); // must be a no-op since a backup already exists

            var restored = svc.ReadBackup();
            Assert.NotNull(restored);
            Assert.True(restored!.User.ContainsKey("SENTINEL"));
            Assert.Equal("keep", restored.User["SENTINEL"]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadBackup_NoBackup_ReturnsNull()
    {
        var (svc, dir) = NewServiceWithTempBackup();
        try { Assert.Null(svc.ReadBackup()); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadBackup_CorruptFile_ReturnsNull()
    {
        var (svc, dir) = NewServiceWithTempBackup();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(svc.BackupPath, "{ this is not valid json ");
            Assert.Null(svc.ReadBackup());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SetVariable_PreservesExpandSz_AndReadsRawTokens_EndToEnd()
    {
        // End-to-end regression for the REG_EXPAND_SZ flattening bug: write an expandable
        // value to a throwaway User variable, then confirm via the service that the value
        // KIND stayed REG_EXPAND_SZ and the RAW %VAR% token round-tripped (not expanded).
        // Uses a unique name in the real HKCU\Environment and removes it in finally.
        var svc = new EnvironmentVariableService();
        var name = "SM_TEST_" + Guid.NewGuid().ToString("N");
        const string rawValue = @"%SystemRoot%\System32;C:\SmTest";
        try
        {
            Assert.True(svc.SetVariable(name, rawValue, EnvVarScope.User));

            using var key = Registry.CurrentUser.OpenSubKey("Environment");
            Assert.NotNull(key);
            Assert.Equal(RegistryValueKind.ExpandString, key!.GetValueKind(name));

            var roundTripped = svc.Read(EnvVarScope.User).Single(v => v.Name == name);
            Assert.Equal(rawValue, roundTripped.Value);   // %SystemRoot% preserved, not expanded
            Assert.True(roundTripped.IsExpandable);
        }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void Read_UserScope_ReturnsSortedNonEmpty()
    {
        // The User environment always contains at least TEMP/Path on a real Windows box,
        // but we only assert structural invariants so the test is robust on CI runners.
        var (svc, dir) = NewServiceWithTempBackup();
        try
        {
            var vars = svc.Read(EnvVarScope.User);
            Assert.All(vars, v => Assert.Equal(EnvVarScope.User, v.Scope));
            Assert.All(vars, v => Assert.False(string.IsNullOrEmpty(v.Name)));
            // sorted, case-insensitive
            var names = vars.Select(v => v.Name).ToList();
            Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // ── P2 #17 regression: TryValidateName, non-throwing restore, kind fidelity ──

    [Theory]
    [InlineData("PATH", true)]
    [InlineData("JAVA_HOME", true)]
    [InlineData("_underscore", true)]
    [InlineData("My.Var", true)]
    [InlineData("Var-1", true)]
    [InlineData("foo(1)", true)]
    public void TryValidateName_AcceptsConformingNames(string name, bool expected)
    {
        var result = EnvironmentVariableService.TryValidateName(name, out var validated);
        Assert.Equal(expected, result);
        if (expected) Assert.Equal(name.Trim(), validated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("=C:")]
    [InlineData("1startsWithDigit")]
    [InlineData("has space")]
    [InlineData("special#char")]
    [InlineData("has@at")]
    [InlineData("plus+sign")]
    [InlineData("with!bang")]
    public void TryValidateName_RejectsNonConforming_ReturnsFalse(string name)
    {
        var result = EnvironmentVariableService.TryValidateName(name, out var validated);
        Assert.False(result);
        Assert.Equal("", validated);
    }

    [Fact]
    public void TryValidateName_RejectsOverlongName()
    {
        var longName = new string('A', 256);
        Assert.False(EnvironmentVariableService.TryValidateName(longName, out _));
    }

    [Fact]
    public void SetVariable_ReturnsFalse_ForInvalidName_DoesNotThrow()
    {
        var svc = new EnvironmentVariableService();

        var result = svc.SetVariable("HAS SPACE", "somevalue", EnvVarScope.User);
        Assert.False(result);

        result = svc.SetVariable("=C:", "somepath", EnvVarScope.User);
        Assert.False(result);

        result = svc.SetVariable("1DIGIT_START", "x", EnvVarScope.User);
        Assert.False(result);
    }

    [Fact]
    public void EnvBackup_WithKinds_RoundTrips()
    {
        var backup = new EnvironmentVariableService.EnvBackup(
            User: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = "%SystemRoot%\\system32",
                ["TEMP"] = "C:\\Temp"
            },
            Machine: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ComSpec"] = "cmd.exe"
            },
            UserKinds: new Dictionary<string, RegistryValueKind>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = RegistryValueKind.ExpandString,
                ["TEMP"] = RegistryValueKind.String
            },
            MachineKinds: new Dictionary<string, RegistryValueKind>(StringComparer.OrdinalIgnoreCase)
            {
                ["ComSpec"] = RegistryValueKind.String
            });

        var json = JsonSerializer.Serialize(backup);
        var restored = JsonSerializer.Deserialize<EnvironmentVariableService.EnvBackup>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.User.Count);
        Assert.Equal("%SystemRoot%\\system32", restored.User["PATH"]);
        Assert.NotNull(restored.UserKinds);
        Assert.Equal(RegistryValueKind.ExpandString, restored.UserKinds!["PATH"]);
        Assert.Equal(RegistryValueKind.String, restored.UserKinds["TEMP"]);
        Assert.NotNull(restored.MachineKinds);
        Assert.Equal(RegistryValueKind.String, restored.MachineKinds!["ComSpec"]);
    }

    [Fact]
    public void EnvBackup_OldFormat_DeserializesWithNullKinds()
    {
        var oldJson = """
        {
            "User": { "PATH": "C:\\Windows" },
            "Machine": { "TEMP": "C:\\Temp" }
        }
        """;

        var restored = JsonSerializer.Deserialize<EnvironmentVariableService.EnvBackup>(oldJson);

        Assert.NotNull(restored);
        Assert.Single(restored!.User);
        Assert.Single(restored.Machine);
        Assert.Null(restored.UserKinds);
        Assert.Null(restored.MachineKinds);
    }

    [Fact]
    public void SetVariable_WithExplicitKind_WritesCorrectKind()
    {
        var svc = new EnvironmentVariableService();
        var name = "SM_TEST_KIND_" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(svc.SetVariable(name, "plain_no_percent", EnvVarScope.User, RegistryValueKind.ExpandString));

            using var key = Registry.CurrentUser.OpenSubKey("Environment");
            Assert.NotNull(key);
            Assert.Equal(RegistryValueKind.ExpandString, key!.GetValueKind(name));
        }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }
}
