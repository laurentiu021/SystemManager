// SysManager · StartupSignatureTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the Startup Manager's signature column — the honest version of the Publisher string.
/// </summary>
/// <remarks>
/// <c>Publisher</c> is <c>FileVersionInfo.CompanyName</c>, which the file declares about itself and any
/// program can set to "Microsoft Corporation". The column these tests cover answers the same question with
/// a certificate instead.
/// <para>What can be exercised end to end is everything except a genuinely signed file: path resolution,
/// the unsigned and unreadable outcomes, the per-path cache, and the wording. A file signed by a publisher
/// this test could choose needs a test certificate and signtool, so the <c>Verified</c> arm is covered at
/// the palette level and by <see cref="AuthenticodeTests"/>, with the gap stated rather than hidden.</para>
/// </remarks>
public class StartupSignatureTests
{
    private static string WriteTempExe(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "sysmgr_startupsig_" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static StartupEntry Entry(string command) => new() { Name = "test", Command = command };

    // ── ResolveExecutablePath: which file an entry actually refers to ──

    [Fact]
    public void ResolveExecutablePath_PrefersTheWholeString_SoASpaceInThePathSurvives()
    {
        // The reason the full-string check comes first: "C:\Program Files\App\app.exe" has a space in the
        // path, and truncating at the first space would resolve to "C:\Program" and find nothing. A real
        // installer writes exactly this.
        var dir = Path.Combine(Path.GetTempPath(), "sysmgr sig probe " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "app with spaces.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        try
        {
            Assert.Equal(exe, StartupService.ResolveExecutablePath(exe));
            Assert.Equal(exe, StartupService.ResolveExecutablePath($"\"{exe}\""));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ResolveExecutablePath_StripsArgumentsWhenTheWholeStringIsNotAFile()
    {
        var exe = WriteTempExe([0x4D, 0x5A]);
        try
        {
            Assert.Equal(exe, StartupService.ResolveExecutablePath($"{exe} --background /silent"));
            Assert.Equal(exe, StartupService.ResolveExecutablePath($"\"{exe}\" --background"));
        }
        finally { File.Delete(exe); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL")]
    [InlineData(@"C:\does\not\exist\ghost.exe")]
    public void ResolveExecutablePath_ReturnsEmptyWhenNothingResolves(string command)
        => Assert.Equal("", StartupService.ResolveExecutablePath(command));

    // ── VerifySignatures: the three states, and the one that must stay silent ──

    [Fact]
    public void VerifySignatures_UnresolvableCommand_LeavesTheEntrySilent()
    {
        // Unknown must render nothing. SignatureDetail is what the pill's Visibility follows, so an empty
        // detail is the mechanism, not an accident — a grey "unknown" pill would be a verdict on a program
        // this scan never looked at.
        var entry = Entry(@"C:\does\not\exist\ghost.exe --run");

        StartupService.VerifySignatures([entry]);

        Assert.Equal(SignatureTrust.Unknown, entry.Signature);
        Assert.Equal("", entry.SignatureDetail);
    }

    [Fact]
    public void VerifySignatures_UnsignedFile_ReadsAsOrdinaryRatherThanAlarming()
    {
        var exe = WriteTempExe([0x4D, 0x5A, 0x90, 0x00]);
        try
        {
            var entry = Entry(exe);

            StartupService.VerifySignatures([entry]);

            Assert.Equal(SignatureTrust.Unsigned, entry.Signature);
            // The copy carries the reassurance explicitly. Most entries on an ordinary machine land here,
            // and a user who reads "unsigned" as "malware" turns off things she needs.
            Assert.Contains("does not mean it is unsafe", entry.SignatureDetail, StringComparison.Ordinal);
        }
        finally { File.Delete(exe); }
    }

    /// <summary>
    /// A file Windows cannot parse at all reads as unsigned, not as a problem.
    /// </summary>
    /// <remarks>
    /// This asserts the opposite of what it used to, and the change is the point. The old mechanism read
    /// the certificate itself, so an empty file surfaced as "signature data present but unreadable" and got
    /// an amber chip — an accusation produced by the reader failing, not by anything about the file.
    /// <para>Measured against <c>WinVerifyTrust</c> instead: an empty file, a two-byte stub, a text file
    /// named <c>.exe</c>, a path that does not exist and even a directory all return
    /// <c>TRUST_E_NOSIGNATURE</c>. Windows declines to accuse anything it cannot parse, and the column now
    /// says the same. That is why nothing synthetic can produce the amber state — see
    /// <see cref="WindowsTrustTests"/> for the mapping, which is where the amber arm is actually pinned.</para>
    /// </remarks>
    [Fact]
    public void VerifySignatures_FileWindowsCannotParse_ReadsAsUnsignedRatherThanAccused()
    {
        var exe = WriteTempExe([]);
        try
        {
            var entry = Entry(exe);

            StartupService.VerifySignatures([entry]);

            Assert.Equal(SignatureTrust.Unsigned, entry.Signature);
            Assert.Contains("nothing to check", entry.SignatureDetail, StringComparison.Ordinal);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public void VerifySignatures_SeveralEntriesOnOneExecutable_AllGetTheSameVerdict()
    {
        // An updater and its tray helper pointing at one exe is ordinary. The per-path cache is there
        // because chain building is the expensive half; this pins that caching does not leave later
        // entries blank, which is how a cache keyed on the wrong thing fails.
        var exe = WriteTempExe([0x4D, 0x5A, 0x90, 0x00]);
        try
        {
            var first = Entry(exe);
            var second = Entry($"{exe} --tray");
            var third = Entry($"\"{exe}\"");

            StartupService.VerifySignatures([first, second, third]);

            Assert.Equal(SignatureTrust.Unsigned, first.Signature);
            Assert.Equal(SignatureTrust.Unsigned, second.Signature);
            Assert.Equal(SignatureTrust.Unsigned, third.Signature);
            Assert.Equal(first.SignatureDetail, third.SignatureDetail);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public void VerifySignatures_DoesNotTouchThePublisherString()
    {
        // Deliberately separate fields. A file whose declared company and whose certificate disagree is the
        // case this column exists for, so overwriting one with the other would destroy the finding.
        var exe = WriteTempExe([0x4D, 0x5A]);
        try
        {
            var entry = Entry(exe);
            entry.Publisher = "Microsoft Corporation";

            StartupService.VerifySignatures([entry]);

            Assert.Equal("Microsoft Corporation", entry.Publisher);
            Assert.Equal(SignatureTrust.Unsigned, entry.Signature);
        }
        finally { File.Delete(exe); }
    }

    /// <summary>
    /// The scan still routes its verdict through the shared describer rather than growing its own copy.
    /// </summary>
    /// <remarks>
    /// The revocation mode itself moved with the chain build and is pinned by
    /// <see cref="AuthenticodeTests.TheInformationalPath_AsksForOfflineRevocation_SoItDoesNotFetchPerFile"/>.
    /// What is this tab's business is that it keeps ASKING the shared describer: the wording of a verdict is
    /// user-facing copy, and a local reimplementation here would compile, pass, and leave two tabs
    /// describing one certificate in two different sentences.
    /// </remarks>
    [Fact]
    public void TheScan_GetsItsVerdictFromTheSharedDescriber()
    {
        var source = File.ReadAllText(ServiceSourcePath("StartupService.cs"));

        Assert.Contains("SignatureVerdict.Describe(path)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Authenticode.ReadSigner", source, StringComparison.Ordinal);
    }

    // Walks up to the app project — source is not copied to the test output.
    private static string ServiceSourcePath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "Services")))
            dir = dir.Parent;

        Assert.NotNull(dir);   // else the assertions above would silently test nothing
        var path = Path.Combine(dir!.FullName, "SysManager", "Services", fileName);
        Assert.True(File.Exists(path), $"{fileName} not found at {path}");
        return path;
    }

    // ── CommonName: the words that end up in the tooltip ──

    [Theory]
    [InlineData("CN=Google LLC, O=Google LLC, L=Mountain View, S=California, C=US", "Google LLC")]
    [InlineData("CN=Mozilla Corporation, OU=Firefox, C=US", "Mozilla Corporation")]
    [InlineData("O=Some Org, CN=Trailing Name", "Trailing Name")]
    [InlineData("CN=Only Name", "Only Name")]
    [InlineData("cn=Lowercase Marker, C=US", "Lowercase Marker")]
    public void CommonName_TakesJustTheCommonName(string subject, string expected)
        => Assert.Equal(expected, Helpers.SignatureVerdict.CommonName(subject));

    [Fact]
    public void CommonName_QuotedNameContainingAComma_IsKeptWhole()
    {
        // "Acme, Inc." is a real shape for a company name, and splitting on the comma would render
        // "comes from Acme" — a different company.
        Assert.Equal("Acme, Inc.",
            Helpers.SignatureVerdict.CommonName("CN=\"Acme, Inc.\", O=Acme, C=US"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("O=No Common Name Here", "O=No Common Name Here")]
    public void CommonName_WithoutAUsableCn_FallsBackWithoutInventing(string subject, string expected)
        => Assert.Equal(expected, Helpers.SignatureVerdict.CommonName(subject));

    // ── The palette: what the user reads, and how loudly ──

    [Theory]
    [InlineData(SignatureTrust.Verified, "Verified")]
    [InlineData(SignatureTrust.Unsigned, "Unsigned")]
    [InlineData(SignatureTrust.Invalid, "Check failed")]
    [InlineData(SignatureTrust.Unknown, "")]
    public void Label_SaysWhatTheStateMeans(SignatureTrust trust, string expected)
        => Assert.Equal(expected, SignatureTrustPalette.Label(trust));

    [Fact]
    public void Label_ForInvalid_DoesNotAccuse()
    {
        // "Check failed", not "Invalid" or "Untrusted". The file may be fine and the machine's certificate
        // store out of date; what the user needs to know is that the answer did not come back.
        var label = SignatureTrustPalette.Label(SignatureTrust.Invalid);

        Assert.DoesNotContain("Invalid", label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Untrusted", label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unsafe", label, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Unsigned is neutral grey and Invalid is the warning colour — not the other way round, and not both.
    /// </summary>
    /// <remarks>
    /// The reasoning is <see cref="ProcessSafetyPalette"/>'s, about unrecognised processes: most startup
    /// entries on an ordinary machine are unsigned, so tinting each of them amber would make the column
    /// read as a list of problems and teach the user to ignore it. Asserted on the theme KEY rather than a
    /// brush instance, because the resolved brush depends on whether a WPF Application exists.
    /// </remarks>
    [Fact]
    public void ThePalette_KeepsUnsignedNeutral_AndWarnsOnlyOnAFailedCheck()
    {
        Assert.Null(SignatureTrustPalette.TextBrushKey(SignatureTrust.Unsigned).Key);
        Assert.Null(SignatureTrustPalette.TextBrushKey(SignatureTrust.Unknown).Key);

        Assert.Equal("SuccessText", SignatureTrustPalette.TextBrushKey(SignatureTrust.Verified).Key);
        Assert.Equal("SuccessBgSubtle", SignatureTrustPalette.BackgroundBrushKey(SignatureTrust.Verified).Key);

        Assert.Equal("WarningText", SignatureTrustPalette.TextBrushKey(SignatureTrust.Invalid).Key);
        Assert.Equal("WarningBgSubtle", SignatureTrustPalette.BackgroundBrushKey(SignatureTrust.Invalid).Key);
    }

    [Fact]
    public void ThePalette_AcceptsWhateverADataGridCellHandsIt()
    {
        // A recycling row can pass the enum, its name, or null. Falling through to Unknown keeps a stale
        // cell blank rather than showing another row's verdict.
        Assert.Equal("Verified", SignatureTrustPalette.Label("Verified"));
        Assert.Equal("Verified", SignatureTrustPalette.Label("verified"));
        Assert.Equal("", SignatureTrustPalette.Label(null));
        Assert.Equal("", SignatureTrustPalette.Label("not an enum member"));
        Assert.Equal("", SignatureTrustPalette.Label(42));
    }
}
