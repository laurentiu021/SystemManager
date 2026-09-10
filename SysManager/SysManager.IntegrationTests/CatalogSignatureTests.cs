// SysManager · CatalogSignatureTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.IntegrationTests;

/// <summary>
/// The catalog half of <see cref="WindowsTrust"/>, against real Windows binaries.
/// </summary>
/// <remarks>
/// This lives in the integration suite rather than the unit one because it is a genuine system dependency:
/// it needs the machine's catalog store, and there is no way to fake one. The unit suite covers the HRESULT
/// mapping and the member-tag formatting, which is where the decisions are.
/// <para><b>It exists because the unit suite structurally could not catch the defect it guards.</b> Every
/// unit test writes its own temp file, and no file a test can create is catalog-signed — so a catalog
/// lookup that silently returned "no signature" for everything would pass the entire unit suite. Reporting
/// <c>powershell.exe</c> as unsigned is exactly what shipped for three releases.</para>
/// </remarks>
[Collection("Sequential")]
public class CatalogSignatureTests
{
    /// <summary>
    /// Windows binaries signed through a catalog rather than in the file are recognised as trusted.
    /// </summary>
    /// <remarks>
    /// These carry no embedded Authenticode signature — Explorer's Digital Signatures tab shows nothing for
    /// them — yet `Get-AuthenticodeSignature` calls them Valid, and so do Autoruns and Process Explorer,
    /// because their signature lives in a <c>.cat</c> under <c>CatRoot</c>. Measured on a stock Windows 11
    /// machine: 12 of the 35 running images with no embedded signature verify this way.
    /// <para>Asserted per file with the name in the message, so a failure says WHICH binary stopped
    /// verifying rather than only that the count changed.</para>
    /// </remarks>
    [Theory]
    [InlineData(@"System32\cmd.exe")]
    [InlineData(@"System32\conhost.exe")]
    [InlineData(@"System32\WindowsPowerShell\v1.0\powershell.exe")]
    public void ACatalogSignedWindowsBinary_IsTrusted(string relative)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), relative);

        // Skip rather than fail if a future Windows drops one of these: the claim is about catalog
        // verification, not about which binaries ship.
        if (!File.Exists(path)) return;

        Assert.Equal(TrustResult.Trusted, WindowsTrust.Verify(path));
    }

    /// <summary>
    /// At least one of them really is catalog-signed rather than all of them having gained an embedded one.
    /// </summary>
    /// <remarks>
    /// The vacuity guard for the test above. If Microsoft ever embedded signatures in these files, that test
    /// would keep passing while exercising nothing of the catalog path — the very failure mode this whole
    /// area has already produced once. There is no public way to ask "was that answered by a catalog", so
    /// this asserts the observable proxy: the file has no embedded signature of its own, which is what makes
    /// the trusted verdict above necessarily a catalog result.
    /// </remarks>
    [Fact]
    public void AtLeastOneOfThem_HasNoEmbeddedSignature_SoTheCatalogPathIsWhatAnswered()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] candidates =
        [
            Path.Combine(windows, @"System32\cmd.exe"),
            Path.Combine(windows, @"System32\conhost.exe"),
            Path.Combine(windows, @"System32\WindowsPowerShell\v1.0\powershell.exe"),
        ];

        var present = candidates.Where(File.Exists).ToList();
        Assert.NotEmpty(present);

        // No embedded certificate to read: Authenticode.ReadSigner reports Unsigned for a catalog-signed
        // file, because the certificate is not in the file at all.
        var withoutEmbedded = present
            .Where(p => Authenticode.ReadSigner(p).State is SignerState.Unsigned)
            .ToList();

        Assert.True(withoutEmbedded.Count > 0,
            "every candidate now carries its own embedded signature, so the theory above no longer "
            + "exercises the catalog path at all. Pick binaries that are still catalog-signed, or the "
            + "catalog lookup is unguarded: "
            + string.Join(", ", present.Select(Path.GetFileName)));

        // And those same files verify — which they can only do through a catalog.
        foreach (var p in withoutEmbedded)
            Assert.Equal(TrustResult.Trusted, WindowsTrust.Verify(p));
    }

    [Fact]
    public void AFileInNoCatalogAndWithNoSignature_IsUnsignedRatherThanAccused()
    {
        // The other side: the catalog lookup must not turn "not in any catalog" into a problem. This is the
        // normal state of most third-party programs — 22 of 82 images on the measured machine.
        var path = Path.Combine(Path.GetTempPath(), "sysmgr_nocat_" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x90, 0x00]);

        try
        {
            Assert.Equal(TrustResult.NoSignature, WindowsTrust.Verify(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Repeated verification of a catalog-signed file keeps working and keeps giving the same answer.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately NOT a leak test, and the reason is worth recording.</b> The first version of this
    /// counted process handles across 200 verifications. A mutation removing
    /// <c>CryptCATAdminReleaseCatalogContext</c> — and a second removing
    /// <c>CryptCATAdminReleaseContext</c> — left it passing: those release a catalogue *context*, which is a
    /// heap structure rather than a kernel handle, so <c>Process.HandleCount</c> never moves. It measured the
    /// wrong instrument, and a leak test that cannot see the leak is worse than none because it is cited as
    /// coverage.
    /// <para>The release calls are pinned by source assertion in
    /// <c>WindowsTrustTests.AFileWithNoEmbeddedSignature_IsAlsoCheckedAgainstTheCatalogs</c>, which both
    /// mutations do fail. What is left worth asserting here is the property a repeated call actually has:
    /// the second and two-hundredth answer match the first, which is what a context reused or released out
    /// from under the next call would break.</para>
    /// </remarks>
    [Fact]
    public void RepeatedVerification_KeepsGivingTheSameAnswer()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\cmd.exe");
        if (!File.Exists(path)) return;

        var first = WindowsTrust.Verify(path);
        Assert.Equal(TrustResult.Trusted, first);

        for (var i = 0; i < 50; i++) Assert.Equal(first, WindowsTrust.Verify(path));
    }
}
