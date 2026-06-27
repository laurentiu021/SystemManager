// SysManager · UpdateApplierTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the in-process self-update applier that replaced the external
/// updater .cmd script (updater TOCTOU hardening). The previous design wrote a
/// batch file to a writable folder and ran it via cmd.exe, leaving a window for
/// a same-user process to swap the script before it executed. The applier runs
/// from inside the downloaded executable instead — there is no on-disk script to
/// tamper with. These tests pin the argument round-trip, quote rejection, and
/// the atomic copy-into-place behaviour.
/// </summary>
public class UpdateApplierTests
{
    [Fact]
    public void BuildArguments_RoundTripsThroughTryParse()
    {
        const string target = @"C:\Program Files\SysManager\SysManager.exe";
        var argString = UpdateApplier.BuildArguments(target, 4321);

        // The OS splits the command line and strips the quotes around the path,
        // so simulate that by parsing the unquoted tokens the way Main receives them.
        var args = new[] { UpdateApplier.ApplyUpdateArg, target, "4321" };
        Assert.True(UpdateApplier.TryParseArgs(args, out var parsedTarget, out var pid));
        Assert.Equal(target, parsedTarget);
        Assert.Equal(4321, pid);

        // The built string carries the sentinel and a quoted path.
        Assert.StartsWith(UpdateApplier.ApplyUpdateArg, argString);
        Assert.Contains($"\"{target}\"", argString);
    }

    [Fact]
    public void BuildArguments_RejectsQuoteInPath()
    {
        Assert.Throws<InvalidOperationException>(
            () => UpdateApplier.BuildArguments(@"C:\evil"" & calc & "".exe", 1));
    }

    public static IEnumerable<object[]> MalformedArgs() => new[]
    {
        new object[] { Array.Empty<string>() },                          // empty
        new object[] { new[] { "--apply-update" } },                     // missing target + pid
        new object[] { new[] { "--apply-update", @"C:\x.exe" } },        // missing pid
        new object[] { new[] { "--other", @"C:\x.exe", "123" } },        // wrong sentinel
        new object[] { new[] { "--apply-update", @"C:\x.exe", "0" } },   // non-positive pid
        new object[] { new[] { "--apply-update", @"C:\x.exe", "abc" } }, // non-numeric pid
        new object[] { new[] { "--apply-update", "", "123" } },          // blank target
    };

    [Theory]
    [MemberData(nameof(MalformedArgs))]
    public void TryParseArgs_RejectsMalformedInput(string[] args)
    {
        Assert.False(UpdateApplier.TryParseArgs(args, out _, out _));
    }

    [Fact]
    public void TryParseArgs_IsCaseInsensitiveOnSentinel()
    {
        var args = new[] { "--APPLY-UPDATE", @"C:\x.exe", "7" };
        Assert.True(UpdateApplier.TryParseArgs(args, out var target, out var pid));
        Assert.Equal(@"C:\x.exe", target);
        Assert.Equal(7, pid);
    }

    [Fact]
    public void ApplyCopy_ReplacesTargetWithSourceContents()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierTest_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            File.WriteAllText(source, "NEW-BUILD");
            File.WriteAllText(target, "OLD-BUILD");

            var ok = UpdateApplier.ApplyCopy(source, target, maxAttempts: 1, delayMs: 0);

            Assert.True(ok);
            Assert.Equal("NEW-BUILD", File.ReadAllText(target));
            // The staging sibling is moved (not left behind) on success.
            Assert.False(File.Exists(target + ".new"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ApplyCopy_CreatesTargetWhenMissing()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierTest_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            File.WriteAllText(source, "NEW-BUILD");

            var ok = UpdateApplier.ApplyCopy(source, target, maxAttempts: 1, delayMs: 0);

            Assert.True(ok);
            Assert.Equal("NEW-BUILD", File.ReadAllText(target));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ApplyCopy_LeavesTargetIntactWhenSourceMissing()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierTest_");
        try
        {
            var source = Path.Combine(dir.FullName, "does-not-exist.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            File.WriteAllText(target, "OLD-BUILD");

            // A missing source is non-recoverable: the early-exit guard returns false
            // immediately, so even a large maxAttempts must NOT spin the retry/backoff
            // loop (it would otherwise misread FileNotFoundException as a transient lock).
            // delayMs is large on purpose — if the guard regressed and the loop ran, the
            // test would hang noticeably rather than return instantly.
            var ok = UpdateApplier.ApplyCopy(source, target, maxAttempts: 10, delayMs: 10_000);

            // A failed copy must never destroy the working executable.
            Assert.False(ok);
            Assert.Equal("OLD-BUILD", File.ReadAllText(target));
            Assert.False(File.Exists(target + ".new"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
