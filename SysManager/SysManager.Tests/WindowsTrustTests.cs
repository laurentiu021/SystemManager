// SysManager · WindowsTrustTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="WindowsTrust"/> — what Windows concludes about a file's signature, and how that
/// HRESULT becomes the thing a user reads.
/// </summary>
/// <remarks>
/// <see cref="WindowsTrust.Classify"/> is where the amber chip is decided, so it is tested directly rather
/// than through a file. Producing a file that is signed-but-untrusted on demand needs a certificate and
/// signtool; the HRESULT that such a file returns does not, and it is the HRESULT that drives the colour.
/// <para>That split is exactly what the previous mechanism got wrong. Its wrong verdicts came from the
/// classification, not from the reading, and no test looked at classification in isolation — so 47 of 48
/// signed programs were labelled "Check failed" through two releases with a green suite.</para>
/// </remarks>
public class WindowsTrustTests
{
    // The HRESULTs, named. Written as literals here on purpose: the production constants are private, and a
    // test that imported them would agree with a typo instead of catching one.
    private const int SOk = 0;
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int CertEExpired = unchecked((int)0x800B0101);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int TrustEExplicitDistrust = unchecked((int)0x800B0111);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int CertERevoked = unchecked((int)0x800B010C);

    [Fact]
    public void SOk_IsTheOnlyTrustedAnswer()
        => Assert.Equal(TrustResult.Trusted, WindowsTrust.Classify(SOk));

    /// <summary>
    /// The three "there is nothing here to check" results are grouped, and that grouping is the fix.
    /// </summary>
    /// <remarks>
    /// <c>TRUST_E_NOSIGNATURE</c> is the ordinary one. The other two come back for a file the Authenticode
    /// provider does not handle, which is also "nothing to check" rather than "suspect". Classifying those
    /// as a failure would put an amber chip on ordinary files and teach the user to ignore the column — the
    /// failure mode this whole mechanism change exists to undo.
    /// </remarks>
    [Theory]
    [InlineData(TrustENoSignature)]
    [InlineData(TrustESubjectFormUnknown)]
    [InlineData(TrustEProviderUnknown)]
    public void NothingToCheck_IsUnsignedRatherThanAFailure(int hresult)
        => Assert.Equal(TrustResult.NoSignature, WindowsTrust.Classify(hresult));

    [Fact]
    public void AnExpiredCertificate_GetsItsOwnState_NotTheGenericFailure()
    {
        // Separate because the sentence a user reads differs: an expired signing certificate on an older
        // program is common and says nothing about the file, while an untrusted root or a modified file
        // does. Collapsing them would make the tooltip lie about one of the two.
        Assert.Equal(TrustResult.Expired, WindowsTrust.Classify(CertEExpired));
        Assert.NotEqual(WindowsTrust.Classify(CertEExpired), WindowsTrust.Classify(CertEUntrustedRoot));
    }

    [Theory]
    [InlineData(CertEUntrustedRoot)]
    [InlineData(TrustEExplicitDistrust)]
    [InlineData(TrustEBadDigest)]
    [InlineData(CertERevoked)]
    public void AGenuineSignatureProblem_IsNotTrusted(int hresult)
        => Assert.Equal(TrustResult.NotTrusted, WindowsTrust.Classify(hresult));

    [Fact]
    public void AnUnrecognisedFailure_IsNotTrusted_RatherThanQuietlyTrusted()
    {
        // The default arm must fail closed. An HRESULT nobody enumerated is a signature Windows would not
        // accept, and mapping the unknown to Trusted is how a check becomes decorative.
        Assert.Equal(TrustResult.NotTrusted, WindowsTrust.Classify(unchecked((int)0x8007000E)));
        Assert.Equal(TrustResult.NotTrusted, WindowsTrust.Classify(unchecked((int)0x80004005)));
    }

    /// <summary>
    /// Every file a test can create comes back unsigned, and that is measured rather than assumed.
    /// </summary>
    /// <remarks>
    /// Windows declines to accuse anything it cannot parse: an empty file, a two-byte stub, a text file
    /// named <c>.exe</c>, a path that does not exist and a directory all return <c>TRUST_E_NOSIGNATURE</c>.
    /// That is worth pinning, because it is the property that makes the amber state unreachable by accident
    /// — the previous mechanism produced it from an unparseable file, which is how the column filled with
    /// warnings about ordinary things.
    /// </remarks>
    [Fact]
    public void NothingUnparseable_IsEverAccused()
    {
        var empty = WriteTemp([]);
        var stub = WriteTemp([0x4D, 0x5A]);
        var text = WriteTemp(System.Text.Encoding.ASCII.GetBytes("not a program at all"));
        var missing = Path.Combine(Path.GetTempPath(), "sysmgr_absent_" + Guid.NewGuid().ToString("N") + ".exe");

        try
        {
            foreach (var path in new[] { empty, stub, text, missing, Path.GetTempPath() })
                Assert.Equal(TrustResult.NoSignature, WindowsTrust.Verify(path));
        }
        finally
        {
            File.Delete(empty);
            File.Delete(stub);
            File.Delete(text);
        }
    }

    [Fact]
    public void ABlankPath_IsAnsweredWithoutCallingWindows()
    {
        // Not a real question, and the native call would have to marshal a null string to ask it.
        Assert.Equal(TrustResult.NoSignature, WindowsTrust.Verify(""));
        Assert.Equal(TrustResult.NoSignature, WindowsTrust.Verify("   "));
    }

    /// <summary>
    /// A file with no signature of its own gets a second question: the Windows catalogs.
    /// </summary>
    /// <remarks>
    /// Windows signs most of its own components through a <c>.cat</c> file rather than inside the binary, so
    /// <c>WTD_CHOICE_FILE</c> alone reports <c>powershell.exe</c>, <c>cmd.exe</c> and <c>conhost.exe</c> as
    /// unsigned — 12 of the 35 running images with no embedded signature verify through a catalog.
    /// <para>Asserted against the source because no file a unit test can create is catalog-signed: a catalog
    /// lookup that silently answered "no signature" for everything would pass this entire suite. The real
    /// verification is <c>CatalogSignatureTests</c> in the integration project, against actual Windows
    /// binaries; what belongs here is that the second question is asked at all, and asked ONLY for
    /// <see cref="TrustResult.NoSignature"/>.</para>
    /// <para>That last part is a decision, not a detail: falling back for an expired or untrusted embedded
    /// signature would be choosing the more flattering of two verdicts.</para>
    /// </remarks>
    [Fact]
    public void AFileWithNoEmbeddedSignature_IsAlsoCheckedAgainstTheCatalogs()
    {
        var source = File.ReadAllText(Path.Combine(AppProjectDir(), "Helpers", "WindowsTrust.cs"));
        Assert.True(source.Length > 3000, "WindowsTrust.cs is too small to be the real file");

        // The fallback happens, and only on the no-signature answer.
        Assert.Contains("embedded is TrustResult.NoSignature ? VerifyByCatalog(filePath) : embedded",
            source, StringComparison.Ordinal);

        // SHA-256 by name. The older CryptCATAdminAcquireContext implies SHA-1 and must not come back.
        Assert.Contains("CryptCATAdminAcquireContext2", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CryptCATAdminAcquireContext(", source, StringComparison.Ordinal);
        Assert.Contains("Sha256 = \"SHA256\"", source, StringComparison.Ordinal);

        // Both handles released. Three of the five native steps can fail, so these belong in a finally and
        // a leak here is once per unsigned file per refresh on a tab that polls.
        Assert.Contains("CryptCATAdminReleaseCatalogContext(admin, catalog, 0)", source, StringComparison.Ordinal);
        Assert.Contains("CryptCATAdminReleaseContext(admin, 0)", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The member tag is upper-case hex — the documented shape, and deliberately not claimed to be more.
    /// </summary>
    /// <remarks>
    /// This started out asserting that upper case was load-bearing, on the reasoning that the tag is matched
    /// against what the catalog stores. A mutation refuted it: forcing the tag to lower case left every real
    /// catalog verification in <c>CatalogSignatureTests</c> passing, so the trust provider is not matching on
    /// this string in the way its name suggests.
    /// <para>Kept, because pinning the documented format is worth a line and a future change to it should be
    /// deliberate — but the docstring says what the evidence supports rather than what sounded right. A test
    /// whose stated reason is false is worse than no test, because it survives review on the strength of the
    /// reason.</para>
    /// </remarks>
    [Fact]
    public void TheMemberTag_IsUpperCaseHex()
    {
        var bytes = new byte[] { 0x0A, 0xBC, 0xDE, 0xF0, 0x00, 0xFF };
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, buffer, bytes.Length);

            var tag = WindowsTrust.HexTag(buffer, (uint)bytes.Length);

            Assert.Equal("0ABCDEF000FF", tag);
            Assert.Equal(tag.ToUpperInvariant(), tag);   // stated directly rather than left to the literal
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// The call asks for no revocation lookup and answers from this machine only.
    /// </summary>
    /// <remarks>
    /// This is the whole "a column makes no network request" guarantee, and it is a property of three
    /// flags rather than of anything observable from a passing test: dropping
    /// <c>WTD_CACHE_ONLY_URL_RETRIEVAL</c> compiles, returns the same verdicts on a connected machine, and
    /// is visible only as a tab that stalls on a train. The previous mechanism's equivalent guarantee was
    /// asserted the same way and is the reason that regression was caught at all.
    /// <para>Asserted against the source because the flags go into a native struct the managed side cannot
    /// read back. The population floor keeps it from passing on a file that no longer contains the call.</para>
    /// </remarks>
    [Fact]
    public void TheTrustCall_AsksForNoNetwork()
    {
        var source = File.ReadAllText(Path.Combine(AppProjectDir(), "Helpers", "WindowsTrust.cs"));
        Assert.True(source.Length > 2000, "WindowsTrust.cs is too small to be the real file");

        // Revocation off: a lookup per file is what a list cannot afford.
        Assert.Contains("fdwRevocationChecks = WtdRevokeNone", source, StringComparison.Ordinal);
        Assert.Contains("WtdRevocationCheckNone", source, StringComparison.Ordinal);

        // And the one that actually stops the trust provider fetching: answer from local caches only.
        Assert.Contains("WtdCacheOnlyUrlRetrieval", source, StringComparison.Ordinal);
        Assert.Contains("dwProvFlags = WtdSaferFlag | WtdCacheOnlyUrlRetrieval | WtdRevocationCheckNone",
            source, StringComparison.Ordinal);

        // No UI, ever: WinVerifyTrust will otherwise put a modal dialog on screen from a background scan.
        Assert.Contains("dwUIChoice = WtdUiNone", source, StringComparison.Ordinal);

        // The state the verify call allocates has to be released, once per file.
        Assert.Contains("Close(data)", source, StringComparison.Ordinal);
        Assert.Contains("WtdStateActionClose", source, StringComparison.Ordinal);
    }

    // Walks up to the app project — source is not copied to the test output.
    private static string AppProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "Helpers")))
            dir = dir.Parent;

        Assert.NotNull(dir);   // else the assertions above would silently test nothing
        return Path.Combine(dir!.FullName, "SysManager");
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "sysmgr_trust_" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
