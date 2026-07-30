// SysManager · EnvironmentVariableServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="EnvironmentVariableService"/>. The pure helpers (name validation,
/// PATH split/join/dedup), bounded file parsing, and redirected-registry backup/restore
/// behavior are exercised deterministically. Tests that need real environment integration
/// use unique HKCU values and never touch the production Machine hive.
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

    [Fact]
    public void ChooseKind_UnsupportedExistingKindFallsBackToStringKind()
    {
        Assert.Equal(
            RegistryValueKind.String,
            EnvironmentVariableService.ChooseKind(RegistryValueKind.DWord, "plain"));
        Assert.Equal(
            RegistryValueKind.ExpandString,
            EnvironmentVariableService.ChooseKind(RegistryValueKind.MultiString, "%SystemRoot%"));
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

    [Fact]
    public void HasBackup_FalseBeforeEnsure_TrueAfter()
    {
        using var env = new RedirectedEnvironment();

        Assert.False(env.Service.HasBackup);
        env.Service.EnsureBackup(includeUser: true, includeMachine: false);

        Assert.True(env.Service.HasBackup);
        Assert.True(env.HasUserRegistryBackup());
        Assert.False(File.Exists(env.Service.BackupPath));
    }

    [Fact]
    public void EnsureBackup_DoesNotOverwriteExistingBackup()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "original");
        env.Service.EnsureBackup(includeUser: true, includeMachine: false);
        var originalSnapshot = env.GetUserBackupRaw();

        env.SetUser("SAFE_USER", "changed");
        env.Service.EnsureBackup(includeUser: true, includeMachine: false);

        Assert.Equal(originalSnapshot, env.GetUserBackupRaw());
        Assert.Equal("original", env.Service.ReadBackup()!.User["SAFE_USER"]);
    }

    [Fact]
    public void EnsureBackup_ParameterlessPreservesBothScopeContract()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "user");
        env.SetMachine("SAFE_MACHINE", "machine");

        env.Service.EnsureBackup();

        Assert.True(env.Service.HasUserBackup);
        Assert.True(env.Service.HasMachineBackup);
    }

    [Fact]
    public void ReadBackup_NoBackup_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();

        Assert.Null(env.Service.ReadBackup());
    }

    [Fact]
    public void ReadBackup_CorruptFile_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();
        env.WriteLegacyUserBackup("{ this is not valid json ");

        Assert.Null(env.Service.ReadBackup());
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
        var svc = new EnvironmentVariableService();
        var vars = svc.Read(EnvVarScope.User);
        Assert.All(vars, v => Assert.Equal(EnvVarScope.User, v.Scope));
        Assert.All(vars, v => Assert.False(string.IsNullOrEmpty(v.Name)));
        // sorted, case-insensitive
        var names = vars.Select(v => v.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);
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
        Assert.Equal("C:\\Windows", restored.User["PATH"]);
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

    // ---------- Backup trust boundary ----------

    [Fact]
    public void ReadBackup_NullUserSection_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();
        env.WriteUserBackup("""{"User":null}""");

        Assert.Null(env.Service.ReadBackup());
    }

    [Fact]
    public void ReadBackup_MissingUserSection_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();
        env.WriteUserBackup("""{"Machine":{}}""");

        Assert.Null(env.Service.ReadBackup());
    }

    [Fact]
    public void ReadBackup_WrongUserType_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();
        env.WriteUserBackup("""{"User":[]}""");

        Assert.Null(env.Service.ReadBackup());
    }

    [Fact]
    public void ReadBackup_WindowsPermittedNonUiName_IsAccepted()
    {
        using var env = new RedirectedEnvironment();
        env.WriteUserBackup("""{"User":{"BAD NAME":"value"}}""");

        var backup = env.Service.ReadBackup();

        Assert.NotNull(backup);
        Assert.Equal("value", backup!.User["BAD NAME"]);
    }

    [Fact]
    public void ReadBackup_EmbeddedNullName_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();
        env.WriteUserBackup("""{"User":{"BAD\u0000NAME":"value"}}""");

        Assert.Null(env.Service.ReadBackup());
    }

    [Fact]
    public void ReadBackup_NullValue_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();
        env.WriteUserBackup("""{"User":{"SAFE":null}}""");

        Assert.Null(env.Service.ReadBackup());
    }

    [Fact]
    public void ReadBackup_OversizedValue_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();
        var json = JsonSerializer.Serialize(new
        {
            User = new Dictionary<string, string>
            {
                ["SAFE"] = new string('x', EnvironmentVariableService.MaxVariableValueLength + 1)
            }
        });
        env.WriteUserBackup(json);

        Assert.Null(env.Service.ReadBackup());
    }

    [Fact]
    public void ReadBackup_OversizedFile_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();
        env.WriteUserBackup(new string('x', EnvironmentVariableService.MaxBackupFileBytes + 1));

        Assert.Null(env.Service.ReadBackup());
    }

    [Fact]
    public void ReadBackup_TooManyVariables_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();
        var values = Enumerable.Range(0, EnvironmentVariableService.MaxVariableCount + 1)
            .ToDictionary(i => $"VAR_{i}", _ => "value");
        env.WriteUserBackup(JsonSerializer.Serialize(new { User = values }));

        Assert.Null(env.Service.ReadBackup());
    }

    [Fact]
    public void ReadBackup_UnsupportedRegistryKind_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();
        env.WriteUserBackup(
            """
            {
              "User": { "SAFE": "value" },
              "UserKinds": { "SAFE": 4 }
            }
            """);

        Assert.Null(env.Service.ReadBackup());
    }

    [Fact]
    public void ReadBackup_LegacyNullMachineSection_IsIgnored()
    {
        using var env = new RedirectedEnvironment();
        env.WriteUserBackup(
            """
            {
              "User": { "SAFE": "value" },
              "Machine": null
            }
            """);

        var backup = env.Service.ReadBackup();

        Assert.NotNull(backup);
        Assert.Equal("value", backup!.User["SAFE"]);
    }

    [Fact]
    public void HasMachineBackup_EmptySection_ReturnsFalse()
    {
        using var env = new RedirectedEnvironment();
        env.WriteMachineBackup("""{"Machine":{}}""", RegistryValueKind.String);

        Assert.False(env.Service.HasMachineBackup);
    }

    [Fact]
    public void HasMachineBackup_NullSection_ReturnsFalse()
    {
        using var env = new RedirectedEnvironment();
        env.WriteMachineBackup("""{"Machine":null}""", RegistryValueKind.String);

        Assert.False(env.Service.HasMachineBackup);
    }

    [Fact]
    public void HasMachineBackup_WrongSectionType_ReturnsFalse()
    {
        using var env = new RedirectedEnvironment();
        env.WriteMachineBackup("""{"Machine":[]}""", RegistryValueKind.String);

        Assert.False(env.Service.HasMachineBackup);
    }

    [Fact]
    public void HasMachineBackup_WrongRegistryType_ReturnsFalse()
    {
        using var env = new RedirectedEnvironment();
        env.WriteMachineBackup(1, RegistryValueKind.DWord);

        Assert.False(env.Service.HasMachineBackup);
    }

    [Fact]
    public void HasMachineBackup_OversizedValue_ReturnsFalse()
    {
        using var env = new RedirectedEnvironment();
        var json = JsonSerializer.Serialize(new
        {
            Machine = new Dictionary<string, string>
            {
                ["SAFE"] = new string('x', EnvironmentVariableService.MaxVariableValueLength + 1)
            }
        });
        env.WriteMachineBackup(json, RegistryValueKind.String);

        Assert.False(env.Service.HasMachineBackup);
    }

    [Fact]
    public void HasMachineBackup_UnsupportedRegistryKind_ReturnsFalse()
    {
        using var env = new RedirectedEnvironment();
        env.WriteMachineBackup(
            """
            {
              "Machine": { "SAFE": "value" },
              "MachineKinds": { "SAFE": 4 }
            }
            """,
            RegistryValueKind.String);

        Assert.False(env.Service.HasMachineBackup);
    }

    [Fact]
    public void RestoreFromBackup_LegacyMachineSectionCannotChangeMachineScope()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "current");
        env.SetMachine("SAFE_MACHINE", "current");
        env.WriteUserBackup(
            """
            {
              "User": { "SAFE_USER": "user-backup" },
              "Machine": {
                "SAFE_MACHINE": "attacker-value",
                "ATTACKER_MACHINE": "owned"
              }
            }
            """);

        var result = env.Service.RestoreFromBackup();

        Assert.True(result.HadBackup);
        Assert.Equal("user-backup", env.GetUser("SAFE_USER"));
        Assert.Equal("current", env.GetMachine("SAFE_MACHINE"));
        Assert.Null(env.GetMachine("ATTACKER_MACHINE"));
    }

    [Fact]
    public void EnsureBackup_UserSnapshotDoesNotPreventLaterMachineSnapshot()
    {
        using var env = new RedirectedEnvironment();
        env.SetMachine("SAFE_MACHINE", "original");

        env.Service.EnsureBackup(includeUser: true, includeMachine: false);
        Assert.False(env.Service.HasMachineBackup);

        env.Service.EnsureBackup(includeUser: true, includeMachine: true);
        Assert.True(env.Service.HasMachineBackup);
    }

    [Fact]
    public void RestoreFromBackup_UsesProtectedMachineSnapshotAndDoesNotOverwriteIt()
    {
        using var env = new RedirectedEnvironment();
        env.SetMachine("SAFE_MACHINE", "original");
        env.Service.EnsureBackup(includeUser: true, includeMachine: true);
        Assert.True(env.Service.HasMachineBackup);
        Assert.DoesNotContain("\"Machine\"", Assert.IsType<string>(env.GetUserBackupRaw()));
        Assert.False(File.Exists(env.Service.BackupPath));

        env.SetMachine("SAFE_MACHINE", "changed");
        env.Service.EnsureBackup(includeUser: true, includeMachine: true);
        env.WriteUserBackup(
            """
            {
              "User": {},
              "Machine": { "SAFE_MACHINE": "attacker-value" }
            }
            """);

        var result = env.Service.RestoreFromBackup();

        Assert.True(result.HadBackup);
        Assert.Equal("original", env.GetMachine("SAFE_MACHINE"));
    }

    [Fact]
    public void ReadBackup_ExactDuplicateNames_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();
        env.WriteUserBackup("""{"User":{"PATH":"first","PATH":"second"}}""");

        Assert.Null(env.Service.ReadBackup());
    }

    [Fact]
    public void ReadBackup_CaseVariantDuplicateNames_ReturnsNull()
    {
        using var env = new RedirectedEnvironment();
        env.WriteUserBackup("""{"User":{"PATH":"first","Path":"second"}}""");

        Assert.Null(env.Service.ReadBackup());
    }

    [Fact]
    public void RestoreFromBackup_LegacyNullMachineSection_DoesNotTouchMachineScope()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "current");
        env.SetMachine("SAFE_MACHINE", "current");
        env.WriteUserBackup(
            """
            {
              "User": { "SAFE_USER": "backup" },
              "Machine": null
            }
            """);

        var result = env.Service.RestoreFromBackup();

        Assert.True(result.HadBackup);
        Assert.Equal("backup", env.GetUser("SAFE_USER"));
        Assert.Equal("current", env.GetMachine("SAFE_MACHINE"));
    }

    [Fact]
    public void RestoreFromBackup_LegacyMissingKindsCountsUnsupportedLiveKindWithoutThrowing()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("UNSUPPORTED", 1, RegistryValueKind.DWord);
        env.SetUser("SAFE_USER", "current");
        env.WriteLegacyUserBackup("""
            {
              "User": {
                "UNSUPPORTED": "backup-text",
                "SAFE_USER": "backup-safe"
              }
            }
            """);

        var result = env.Service.RestoreFromBackup();

        Assert.False(result.InvalidBackup);
        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, env.GetUser("UNSUPPORTED"));
        Assert.Equal("backup-safe", env.GetUser("SAFE_USER"));
    }

    [Fact]
    public void EnsureBackup_InvalidUserSnapshotIsNotReplaced()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "current");
        env.WriteUserRegistryBackup("{ invalid json");
        var original = env.GetUserBackupRaw();

        Assert.Throws<InvalidDataException>(() =>
            env.Service.EnsureBackup(includeUser: true, includeMachine: false));

        Assert.Equal(original, env.GetUserBackupRaw());
        Assert.Equal("current", env.GetUser("SAFE_USER"));
    }

    [Fact]
    public void EnsureBackup_InvalidMachineSnapshotIsNotReplaced()
    {
        using var env = new RedirectedEnvironment();
        env.SetMachine("SAFE_MACHINE", "current");
        env.WriteMachineBackup("{ invalid json", RegistryValueKind.String);
        var original = env.GetMachineBackupRaw();

        Assert.Throws<InvalidDataException>(() =>
            env.Service.EnsureBackup(includeUser: false, includeMachine: true));

        Assert.Equal(original, env.GetMachineBackupRaw());
        Assert.Equal("current", env.GetMachine("SAFE_MACHINE"));
    }

    [Fact]
    public void RestoreFromBackup_InvalidMachineSnapshotPreventsUserMutation()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "current-user");
        env.SetMachine("SAFE_MACHINE", "current-machine");
        env.WriteUserRegistryBackup("""
            { "User": { "SAFE_USER": "backup-user" } }
            """);
        env.WriteMachineBackup("{ invalid json", RegistryValueKind.String);

        var result = env.Service.RestoreFromBackup();

        Assert.True(result.InvalidBackup);
        Assert.Equal(0, result.Restored);
        Assert.Equal(0, result.Removed);
        Assert.Equal("current-user", env.GetUser("SAFE_USER"));
        Assert.Equal("current-machine", env.GetMachine("SAFE_MACHINE"));
    }

    [Fact]
    public void RestoreFromBackup_EmptyMachineSnapshotPreventsUserMutation()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "current-user");
        env.SetMachine("SAFE_MACHINE", "current-machine");
        env.WriteUserRegistryBackup("""
            { "User": { "SAFE_USER": "backup-user" } }
            """);
        env.WriteMachineBackup("""{ "Machine": {} }""", RegistryValueKind.String);

        var result = env.Service.RestoreFromBackup();

        Assert.True(result.InvalidBackup);
        Assert.Equal(0, result.Restored);
        Assert.Equal(0, result.Removed);
        Assert.Equal("current-user", env.GetUser("SAFE_USER"));
        Assert.Equal("current-machine", env.GetMachine("SAFE_MACHINE"));
    }

    [Fact]
    public void RestoreFromBackup_InvalidUserSnapshotPreventsMachineMutation()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "current-user");
        env.SetMachine("SAFE_MACHINE", "current-machine");
        env.WriteUserRegistryBackup("{ invalid json");
        env.WriteMachineBackup("""
            { "Machine": { "SAFE_MACHINE": "backup-machine" } }
            """, RegistryValueKind.String);

        var result = env.Service.RestoreFromBackup();

        Assert.True(result.InvalidBackup);
        Assert.Equal(0, result.Restored);
        Assert.Equal(0, result.Removed);
        Assert.Equal("current-user", env.GetUser("SAFE_USER"));
        Assert.Equal("current-machine", env.GetMachine("SAFE_MACHINE"));
    }

    [Fact]
    public void EnsureBackup_MachineOnlyChangeDoesNotCreateUserSnapshot()
    {
        using var env = new RedirectedEnvironment();
        env.SetMachine("SAFE_MACHINE", "original");

        env.Service.EnsureBackup(includeUser: false, includeMachine: true);

        Assert.True(env.Service.HasMachineBackup);
        Assert.False(env.Service.HasUserBackup);
        Assert.False(env.HasUserRegistryBackup());
        Assert.False(File.Exists(env.Service.BackupPath));
    }

    [Fact]
    public void EnsureBackup_InvalidUnrequestedUserSnapshotBlocksMachineSnapshot()
    {
        using var env = new RedirectedEnvironment();
        env.SetMachine("SAFE_MACHINE", "original");
        env.WriteUserRegistryBackup("{ invalid json");

        Assert.Throws<InvalidDataException>(() =>
            env.Service.EnsureBackup(includeUser: false, includeMachine: true));

        Assert.Null(env.GetMachineBackupRaw());
    }

    [Fact]
    public void EnsureBackup_LiveWindowsPermittedNonUiName_IsCaptured()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("BAD NAME", "value");

        env.Service.EnsureBackup(includeUser: true, includeMachine: false);

        Assert.Equal("value", env.Service.ReadBackup()!.User["BAD NAME"]);
    }

    [Fact]
    public void EnsureBackup_UnsupportedLiveRegistryKindPublishesNoSnapshot()
    {
        using var env = new RedirectedEnvironment();
        env.SetMachine("UNSUPPORTED", 1, RegistryValueKind.DWord);

        Assert.Throws<InvalidDataException>(() =>
            env.Service.EnsureBackup(includeUser: false, includeMachine: true));

        Assert.Null(env.GetMachineBackupRaw());
    }

    [Fact]
    public void ProtectedMachineBackupAcl_AcceptsOnlyTrustedWriters()
    {
        var security = EnvironmentVariableService.CreateProtectedMachineBackupSecurity();

        Assert.True(EnvironmentVariableService.IsProtectedMachineBackupSecurity(security));

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null);
        security.AddAccessRule(new RegistryAccessRule(
            users,
            RegistryRights.SetValue,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        Assert.False(EnvironmentVariableService.IsProtectedMachineBackupSecurity(security));
    }

    [Fact]
    public void ProtectedMachineBackupAcl_RejectsUntrustedOwner()
    {
        var security = EnvironmentVariableService.CreateProtectedMachineBackupSecurity();
        security.SetOwner(new SecurityIdentifier(
            WellKnownSidType.BuiltinUsersSid,
            domainSid: null));

        Assert.False(EnvironmentVariableService.IsProtectedMachineBackupSecurity(security));
    }

    [Fact]
    public void MachineBackup_UserWritableRegistryPathIsRejected()
    {
        using var env = new RedirectedEnvironment(enforceMachineBackupProtection: true);
        env.SetMachine("SAFE_MACHINE", "current");
        env.WriteMachineBackup("""
            { "Machine": { "SAFE_MACHINE": "attacker-value" } }
            """, RegistryValueKind.String);

        var result = env.Service.RestoreFromBackup();

        Assert.False(env.Service.HasMachineBackup);
        Assert.True(result.InvalidBackup);
        Assert.Equal("current", env.GetMachine("SAFE_MACHINE"));
    }

    [Fact]
    public void MissingMachineBackup_DoesNotBlockUserOnlySnapshotOnUntrustedParent()
    {
        using var env = new RedirectedEnvironment(enforceMachineBackupProtection: true);
        env.SetUser("SAFE_USER", "original");

        Assert.False(env.Service.HasBackup);

        env.Service.EnsureBackup(includeUser: true, includeMachine: false);

        Assert.True(env.Service.HasUserBackup);
        Assert.False(env.Service.HasMachineBackup);
        Assert.Equal("original", env.Service.ReadBackup()!.User["SAFE_USER"]);
    }

    [Fact]
    public void RestoreFromBackup_DeniedMachineWritesLeaveValuesUnchangedAndCountFailures()
    {
        using var env = new RedirectedEnvironment();
        env.SetMachine("SAFE_MACHINE", "original");
        env.Service.EnsureBackup(includeUser: false, includeMachine: true);
        env.SetMachine("SAFE_MACHINE", "changed");
        env.SetMachine("ADDED_MACHINE", "added");

        using var key = env.MachineRoot.OpenSubKey(
            EnvironmentVariableService.MachineEnvPath,
            writable: true);
        Assert.NotNull(key);
        var originalSecurity = key!.GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        var deniedSecurity = key.GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.NotNull(identity.User);
        deniedSecurity.AddAccessRule(new RegistryAccessRule(
            identity.User!,
            RegistryRights.SetValue,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));
        key.SetAccessControl(deniedSecurity);

        try
        {
            var result = env.Service.RestoreFromBackup();

            Assert.False(result.InvalidBackup);
            Assert.Equal(0, result.Restored);
            Assert.Equal(0, result.Removed);
            Assert.Equal(2, result.Failed);
            Assert.Equal("changed", env.GetMachine("SAFE_MACHINE"));
            Assert.Equal("added", env.GetMachine("ADDED_MACHINE"));
        }
        finally
        {
            key.SetAccessControl(originalSecurity);
        }
    }

    [Fact]
    public void ReadBackup_UserRegistryWrongTypeReturnsNullAndIsNotReplaced()
    {
        using var env = new RedirectedEnvironment();
        env.WriteUserRegistryBackup(1, RegistryValueKind.DWord);

        Assert.Null(env.Service.ReadBackup());
        Assert.Throws<InvalidDataException>(() =>
            env.Service.EnsureBackup(includeUser: true, includeMachine: false));
        Assert.Equal(1, env.GetUserBackupRaw());
    }

    private sealed class RedirectedEnvironment : IDisposable
    {
        private readonly string _userRootName =
            $@"Software\SysManagerTests\Environment\User_{Guid.NewGuid():N}";
        private readonly string _machineRootName =
            $@"Software\SysManagerTests\Environment\Machine_{Guid.NewGuid():N}";

        public RedirectedEnvironment(bool enforceMachineBackupProtection = false)
        {
            BackupDirectory = Path.Combine(
                Path.GetTempPath(),
                $"SysManagerEnvironmentTests_{Guid.NewGuid():N}");
            UserRoot = Registry.CurrentUser.CreateSubKey(_userRootName, writable: true)
                ?? throw new InvalidOperationException("Could not create redirected User root.");
            MachineRoot = Registry.CurrentUser.CreateSubKey(_machineRootName, writable: true)
                ?? throw new InvalidOperationException("Could not create redirected Machine root.");

            using var userEnvironment = UserRoot.CreateSubKey(
                EnvironmentVariableService.UserEnvPath,
                writable: true);
            using var machineEnvironment = MachineRoot.CreateSubKey(
                EnvironmentVariableService.MachineEnvPath,
                writable: true);

            Service = new EnvironmentVariableService(
                BackupDirectory,
                UserRoot,
                MachineRoot,
                enforceMachineBackupProtection);
        }

        public string BackupDirectory { get; }
        public RegistryKey UserRoot { get; }
        public RegistryKey MachineRoot { get; }
        public EnvironmentVariableService Service { get; }

        public void WriteUserBackup(string json)
            => WriteLegacyUserBackup(json);

        public void WriteLegacyUserBackup(string json)
        {
            Directory.CreateDirectory(BackupDirectory);
            File.WriteAllText(Service.BackupPath, json);
        }

        public void WriteUserRegistryBackup(object value, RegistryValueKind kind = RegistryValueKind.String)
        {
            using var key = UserRoot.CreateSubKey(
                EnvironmentVariableService.UserBackupPath,
                writable: true);
            key!.SetValue(EnvironmentVariableService.UserBackupValueName, value, kind);
        }

        public bool HasUserRegistryBackup()
        {
            using var key = UserRoot.OpenSubKey(EnvironmentVariableService.UserBackupPath);
            return key?.GetValueNames().Contains(
                EnvironmentVariableService.UserBackupValueName,
                StringComparer.OrdinalIgnoreCase) == true;
        }

        public object? GetUserBackupRaw()
        {
            using var key = UserRoot.OpenSubKey(EnvironmentVariableService.UserBackupPath);
            return key?.GetValue(
                EnvironmentVariableService.UserBackupValueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        public void WriteMachineBackup(object value, RegistryValueKind kind)
        {
            using var key = MachineRoot.CreateSubKey(
                EnvironmentVariableService.MachineBackupPath,
                writable: true);
            key!.SetValue(EnvironmentVariableService.MachineBackupValueName, value, kind);
        }

        public object? GetMachineBackupRaw()
        {
            using var key = MachineRoot.OpenSubKey(EnvironmentVariableService.MachineBackupPath);
            return key?.GetValue(
                EnvironmentVariableService.MachineBackupValueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        public void SetUser(string name, string value)
            => SetUser(name, value, RegistryValueKind.String);

        public void SetUser(string name, object value, RegistryValueKind kind)
        {
            using var key = UserRoot.OpenSubKey(
                EnvironmentVariableService.UserEnvPath,
                writable: true);
            key!.SetValue(name, value, kind);
        }

        public void SetMachine(string name, string value)
            => SetMachine(name, value, RegistryValueKind.String);

        public void SetMachine(string name, object value, RegistryValueKind kind)
        {
            using var key = MachineRoot.OpenSubKey(
                EnvironmentVariableService.MachineEnvPath,
                writable: true);
            key!.SetValue(name, value, kind);
        }

        public object? GetUser(string name)
        {
            using var key = UserRoot.OpenSubKey(EnvironmentVariableService.UserEnvPath);
            return key?.GetValue(name, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        public object? GetMachine(string name)
        {
            using var key = MachineRoot.OpenSubKey(EnvironmentVariableService.MachineEnvPath);
            return key?.GetValue(name, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        public void Dispose()
        {
            UserRoot.Dispose();
            MachineRoot.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_userRootName, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(_machineRootName, throwOnMissingSubKey: false);
            if (Directory.Exists(BackupDirectory))
                Directory.Delete(BackupDirectory, recursive: true);
        }
    }
}
