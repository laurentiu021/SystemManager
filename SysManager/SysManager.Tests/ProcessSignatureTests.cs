// SysManager · ProcessSignatureTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the Process Manager's signature column — who really made the running image.
/// </summary>
/// <remarks>
/// The Safety chip beside it comes from the bundled process database, keyed on the process NAME, so a copy
/// of <c>svchost.exe</c> in a user folder inherits the real one's chip. This column asks the file itself.
/// <para>Shaped after <see cref="StartupSignatureTests"/> because the two tabs run the same post-pass over
/// the same shared describer; the same gap is stated the same way, too. A genuinely signed file needs a
/// test certificate and signtool, so <c>Verified</c> is covered at the palette level and in
/// <see cref="AuthenticodeTests"/> rather than pretended at here.</para>
/// </remarks>
public class ProcessSignatureTests
{
    private static string WriteTempExe(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "sysmgr_procsig_" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static ProcessEntry Entry(int pid, string path) =>
        new() { Pid = pid, Name = "test", FilePath = path };

    [Fact]
    public void VerifySignatures_AnUnsignedImage_SaysSoWithoutAccusingIt()
    {
        var exe = WriteTempExe([0x4D, 0x5A, 0x90, 0x00]);
        try
        {
            var entry = Entry(1000, exe);

            ProcessManagerService.VerifySignatures([entry]);

            Assert.Equal(SignatureTrust.Unsigned, entry.Signature);
            // The wording matters as much as the state: most user programs are unsigned, and a sentence
            // that read as a warning would make the whole column look like a list of problems.
            Assert.Contains("does not mean it is unsafe", entry.SignatureDetail, StringComparison.Ordinal);
        }
        finally { File.Delete(exe); }
    }

    /// <summary>
    /// A file Windows cannot parse reads as unsigned, and specifically not as a problem.
    /// </summary>
    /// <remarks>
    /// The inverse of what this asserted before the mechanism changed, deliberately. Reading the
    /// certificate ourselves turned an unparseable file into an amber chip, which was the reader failing
    /// rather than anything about the file. <c>WinVerifyTrust</c> returns <c>TRUST_E_NOSIGNATURE</c> for
    /// every such case — measured on an empty file, a two-byte stub, a text file named <c>.exe</c>, a
    /// missing path and a directory — so an image the app cannot make sense of is never accused.
    /// </remarks>
    [Fact]
    public void VerifySignatures_AnImageWindowsCannotParse_ReadsAsUnsignedRatherThanAccused()
    {
        var exe = WriteTempExe([]);
        try
        {
            var entry = Entry(1001, exe);

            ProcessManagerService.VerifySignatures([entry]);

            Assert.Equal(SignatureTrust.Unsigned, entry.Signature);
            Assert.Contains("nothing to check", entry.SignatureDetail, StringComparison.Ordinal);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public void VerifySignatures_AProcessWithNoReadablePath_IsLeftUnknownWithNoPill()
    {
        // The normal case for most system processes without elevation: Process.MainModule throws, FilePath
        // stays empty, and nothing was checked. Unknown renders no chip, so the tab does not put a grey
        // verdict on a program it never looked at.
        var entry = Entry(4, "");

        ProcessManagerService.VerifySignatures([entry]);

        Assert.Equal(SignatureTrust.Unknown, entry.Signature);
        Assert.Equal("", entry.SignatureDetail);
    }

    [Fact]
    public void VerifySignatures_ManyProcessesFromOneExecutable_AllGetTheSameVerdict()
    {
        // The common case, not the exception: one browser runs as a dozen processes from one image. This is
        // what the path-keyed cache is for, and a cache keyed on the wrong thing shows up here as later
        // entries left blank.
        var exe = WriteTempExe([0x4D, 0x5A, 0x90, 0x00]);
        try
        {
            var tabs = Enumerable.Range(0, 12).Select(i => Entry(2000 + i, exe)).ToList();

            ProcessManagerService.VerifySignatures(tabs);

            Assert.All(tabs, e => Assert.Equal(SignatureTrust.Unsigned, e.Signature));
            Assert.All(tabs, e => Assert.Equal(tabs[0].SignatureDetail, e.SignatureDetail));
        }
        finally { File.Delete(exe); }
    }

    /// <summary>
    /// A verdict reached for one entry does not leak onto an entry that was never checked.
    /// </summary>
    /// <remarks>
    /// The negative side of the caching test above, and it had to be rebuilt when the mechanism changed.
    /// It used to pit two synthetic files against each other expecting two different verdicts; under
    /// <c>WinVerifyTrust</c> every synthetic file is <c>TRUST_E_NOSIGNATURE</c>, so no pair of files a test
    /// can create will disagree — which is a better outcome for users and a worse one for that assertion.
    /// <para>What is still fully deterministic, and is the failure that would actually matter: an entry with
    /// no image path must come out of the loop untouched even when an earlier entry in the same call got a
    /// verdict. A cache keyed on nothing, or a verdict variable hoisted out of the loop, shows up here as a
    /// system process wearing another program's signature.</para>
    /// </remarks>
    [Fact]
    public void VerifySignatures_AnEntryWithNoPath_DoesNotInheritTheVerdictBeforeIt()
    {
        var exe = WriteTempExe([0x4D, 0x5A, 0x90, 0x00]);
        try
        {
            var checkedFirst = Entry(3000, exe);
            var noPath = Entry(3001, "");
            var checkedLast = Entry(3002, exe);

            ProcessManagerService.VerifySignatures([checkedFirst, noPath, checkedLast]);

            Assert.Equal(SignatureTrust.Unsigned, checkedFirst.Signature);
            Assert.Equal(SignatureTrust.Unknown, noPath.Signature);
            Assert.Equal("", noPath.SignatureDetail);

            // And the entry after the gap still gets its own answer, from the cache rather than a re-read.
            Assert.Equal(SignatureTrust.Unsigned, checkedLast.Signature);
            Assert.Equal(checkedFirst.SignatureDetail, checkedLast.SignatureDetail);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public void VerifySignatures_LeavesTheDatabaseSafetyChipAlone()
    {
        // Two columns answering two questions. The interesting row is exactly the one where they disagree —
        // a program the database recognises by name, running from an image nobody signed — so one must not
        // overwrite the other.
        var exe = WriteTempExe([0x4D, 0x5A]);
        try
        {
            var entry = Entry(3100, exe);
            entry.SafetyLevel = "System";
            entry.Category = "System";

            ProcessManagerService.VerifySignatures([entry]);

            Assert.Equal("System", entry.SafetyLevel);
            Assert.Equal("System", entry.Category);
            Assert.Equal(SignatureTrust.Unsigned, entry.Signature);
        }
        finally { File.Delete(exe); }
    }

    /// <summary>
    /// A refresh must not blank the column on a row it is only updating.
    /// </summary>
    /// <remarks>
    /// This is the regression the design is most exposed to. A snapshot carries no image path for a PID the
    /// caller already tracks — that is what makes the per-refresh cache cheap — so the fresh entry's verdict
    /// is <see cref="SignatureTrust.Unknown"/> by construction. If <c>ReconcileInto</c> ever copied the
    /// signature pair across with the volatile metrics, the chip would appear on first load and vanish one
    /// tick later, which is the kind of flicker that reads as a rendering bug rather than a lost value.
    /// </remarks>
    [Fact]
    public void ReconcileInto_ASurvivingProcess_KeepsTheSignatureItWasVerifiedWith()
    {
        var target = new BulkObservableCollection<ProcessEntry>();
        var started = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

        var tracked = new ProcessEntry
        {
            Pid = 4242,
            Name = "chrome",
            FilePath = @"C:\Program Files\Contoso\app.exe",
            StartTime = started,
            Signature = SignatureTrust.Verified,
            SignatureDetail = "Windows can confirm this really comes from Contoso Ltd.",
            MemoryBytes = 100,
        };
        target.Add(tracked);

        // What a refresh actually hands over for a PID it already tracks: metrics, no path, no verdict.
        var fresh = new ProcessEntry { Pid = 4242, Name = "chrome", StartTime = started, MemoryBytes = 250 };

        ViewModels.ProcessManagerViewModel.ReconcileInto(target, [fresh]);

        var row = Assert.Single(target);
        Assert.Same(tracked, row);
        Assert.Equal(250, row.MemoryBytes);                                     // the metric did update
        Assert.Equal(SignatureTrust.Verified, row.Signature);                   // the verdict did not
        Assert.Equal("Windows can confirm this really comes from Contoso Ltd.", row.SignatureDetail);
    }

    [Fact]
    public void ReconcileInto_APidWindowsReused_DoesNotInheritTheOldProcessSignature()
    {
        // The other half: a reused PID is a DIFFERENT program, so keeping the row would show the previous
        // one's verified publisher against something entirely unrelated. Start time is what tells them
        // apart, and the row is replaced rather than updated.
        var target = new BulkObservableCollection<ProcessEntry>();
        target.Add(new ProcessEntry
        {
            Pid = 4242,
            Name = "chrome",
            StartTime = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
            Signature = SignatureTrust.Verified,
            SignatureDetail = "Windows can confirm this really comes from Contoso Ltd.",
        });

        var reused = new ProcessEntry
        {
            Pid = 4242,
            Name = "something-else",
            StartTime = new DateTime(2026, 9, 10, 9, 30, 0, DateTimeKind.Utc),
        };

        ViewModels.ProcessManagerViewModel.ReconcileInto(target, [reused]);

        var row = Assert.Single(target);
        Assert.Same(reused, row);
        Assert.Equal(SignatureTrust.Unknown, row.Signature);
        Assert.Equal("", row.SignatureDetail);
    }
}
