// SysManager · AuthenticodeTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security.Cryptography.X509Certificates;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="Authenticode"/>, the shared signer-certificate reader and chain validator.
/// </summary>
/// <remarks>
/// The chain policy used to be written out twice — once in <c>SpeedTestService.VerifyOoklaSignature</c> for
/// a third-party download and once in <c>UpdateService.VerifyAuthenticode</c> for our own installer. Two
/// copies of a security policy drift in the way that matters least visibly: a weakened revocation mode in
/// one of them changes the posture while every test stays green, because nothing compares them.
/// <para>What can be asserted by execution here is the three-way split
/// <see cref="Authenticode.ReadSigner"/> returns, which is the part both callers depend on and disagree
/// about. Chain validation itself needs a genuinely signed file with a controllable publisher — a test
/// certificate and signtool, which this project does not have — so that half is pinned against the source,
/// with the limitation stated rather than papered over.</para>
/// </remarks>
public class AuthenticodeTests
{
    private static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "sysmgr_authcode_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ── ReadSigner: the three-way split the two callers disagree about ──

    [Fact]
    public void ReadSigner_FileWithNoSignature_ReportsUnsigned()
    {
        // CRYPT_E_NO_MATCH, not null and not a generic failure. Conflating this with a read error is the
        // bug that once made the in-app updater reject every unsigned build it downloaded.
        var path = WriteTempFile("not a signed PE, just bytes"u8.ToArray());
        try
        {
            var (state, cert, _) = Authenticode.ReadSigner(path);

            Assert.Equal(SignerState.Unsigned, state);
            Assert.Null(cert);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadSigner_PeLikeHeaderWithoutASignatureDirectory_ReportsUnsigned()
    {
        // Bytes that begin like a PE but carry no signature directory: still "no signature", not a
        // malformed-file error. This is the shape of a real unsigned build.
        var bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        var path = WriteTempFile(bytes);
        try
        {
            Assert.Equal(SignerState.Unsigned, Authenticode.ReadSigner(path).State);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadSigner_EmptyFile_ReportsUnreadable_NotUnsigned()
    {
        // An empty file cannot be read as an image at all, and surfaces as a different HResult. The
        // distinction is load-bearing: UpdateService accepts Unsigned and rejects Unreadable, so
        // collapsing the two would make it accept a file it cannot parse.
        var path = WriteTempFile([]);
        try
        {
            var (state, cert, hresult) = Authenticode.ReadSigner(path);

            Assert.Equal(SignerState.Unreadable, state);
            Assert.Null(cert);
            Assert.NotEqual(0, hresult);   // the caller logs this, so it must not be lost
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadSigner_MissingFile_ReportsUnreadable_AndDoesNotThrow()
    {
        // Never throws is part of the contract: the Startup Manager will call this per entry while
        // scanning, where a path that has been uninstalled since the registry value was written is
        // ordinary rather than exceptional.
        var path = Path.Combine(Path.GetTempPath(), "sysmgr_authcode_absent_" + Guid.NewGuid().ToString("N") + ".bin");

        var (state, cert, _) = Authenticode.ReadSigner(path);

        Assert.Equal(SignerState.Unreadable, state);
        Assert.Null(cert);
    }

    // ── ValidateChain: the policy, pinned against the source ──

    /// <summary>
    /// The one chain policy this app uses is the strict one: revocation honoured, root excluded from it,
    /// no verification flag relaxed.
    /// </summary>
    /// <remarks>
    /// Asserted on the policy object itself rather than on the text of the method that used to build it.
    /// The previous version of this guard matched source lines, and it would have passed unchanged through
    /// the defect it now covers: a setting that is simply absent leaves no line to fail on.
    /// </remarks>
    [Theory]
    [InlineData(X509RevocationMode.Online)]
    [InlineData(X509RevocationMode.Offline)]
    public void EveryPolicy_IsTheStrictOne(X509RevocationMode revocation)
    {
        var policy = Authenticode.PolicyFor(revocation);

        Assert.Equal(revocation, policy.RevocationMode);
        Assert.Equal(X509RevocationFlag.ExcludeRoot, policy.RevocationFlag);
        Assert.Equal(X509VerificationFlags.NoFlag, policy.VerificationFlags);
    }

    /// <summary>
    /// The offline policy really is offline: it does not download a missing intermediate certificate
    /// either.
    /// </summary>
    /// <remarks>
    /// <see cref="X509RevocationMode.Offline"/> suppresses the CRL and OCSP fetch and nothing else, so a
    /// chain build under it still followed the Authority Information Access extension and downloaded any
    /// intermediate the local store did not have. That is a network request from a tab the user merely
    /// opened, and on a machine with no network it is a wait per unrecognised issuer — the exact stall the
    /// offline mode was chosen to avoid. The pairing is the whole point of
    /// <see cref="Authenticode.PolicyFor"/>, so it is pinned in both directions: the fail-closed gates
    /// must keep the download, because there a missing intermediate is what a legitimate signature needs
    /// fetched before it can validate.
    /// </remarks>
    [Fact]
    public void OnlyTheOnlinePolicy_MayDownloadACertificate()
    {
        Assert.True(Authenticode.PolicyFor(X509RevocationMode.Offline).DisableCertificateDownloads);
        Assert.False(Authenticode.PolicyFor(X509RevocationMode.Online).DisableCertificateDownloads);
    }

    /// <summary>
    /// The chain build uses that policy, and a build that fails returns false rather than falling through.
    /// </summary>
    /// <remarks>
    /// Still asserted against the source, deliberately and with the reason stated: producing a signed
    /// binary whose chain can be made to fail on demand needs a test certificate and signtool. What CAN be
    /// checked mechanically is that the policy above is the one handed to the chain, and that the failure
    /// path returns.
    /// <para>This guard replaces the equivalent assertions that used to live inside
    /// <c>UpdateServiceAuthenticodeTests.VerifyAuthenticode_PinsThePublisherAndBuildsAChain</c>, which went
    /// RED the moment the chain build moved out of that method — correctly, and it is the reason this one
    /// exists rather than the assertions simply being dropped.</para>
    /// </remarks>
    [Fact]
    public void ValidateChain_BuildsWithThatPolicy_AndFailsClosed()
    {
        var method = HelperMethodSource("internal static bool ValidateChain");

        Assert.Contains("ChainPolicy = PolicyFor(revocation)", method, StringComparison.Ordinal);
        Assert.Contains("chain.Build(certificate)", method, StringComparison.Ordinal);
        Assert.Contains("return false", method, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both fail-closed gates ask for online revocation, and the helper does not decide that for them.
    /// </summary>
    /// <remarks>
    /// The revocation mode is a parameter precisely so a scan of many files can pass <c>Offline</c> without
    /// a network request per file. That flexibility is also the way the strict callers could be weakened
    /// invisibly — passing <c>NoCheck</c> compiles, runs, and silently stops checking revocation on the
    /// installer we download and the third-party binary we execute. So the call sites are pinned, not just
    /// the helper.
    /// </remarks>
    [Theory]
    [InlineData("UpdateService.cs")]
    [InlineData("SpeedTestService.cs")]
    public void EveryFailClosedGate_AsksForOnlineRevocation(string serviceFile)
    {
        var source = File.ReadAllText(ServiceSourcePath(serviceFile));

        var at = source.IndexOf("Authenticode.ValidateChain", StringComparison.Ordinal);
        Assert.True(at >= 0,
            $"{serviceFile} no longer calls Authenticode.ValidateChain — if the verification moved, move "
            + "this guard with it rather than deleting it");

        // The mode sits in the same call, which spans a wrapped line; take enough to cover it and no more.
        var call = source[at..Math.Min(source.Length, at + 220)];
        Assert.Contains("X509RevocationMode.Online", call, StringComparison.Ordinal);
    }

    private static string HelperMethodSource(string signature)
    {
        var source = File.ReadAllText(Path.Combine(AppProjectDir(), "Helpers", "Authenticode.cs"));
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found in Helpers/Authenticode.cs — this test would "
            + "otherwise assert nothing at all");

        // To the next member declared at the same indentation, or the end of the file when the requested
        // one is last. Slicing to the end unconditionally was fine while ValidateChain WAS last, and
        // stopped being fine the moment a member was added after it: the slice then also covered that
        // member's body, and an assertion could pass on text belonging to a method it was not about.
        var body = source[start..];
        var next = body.IndexOf("\n    internal static ", 1, StringComparison.Ordinal);
        var method = next > 0 ? body[..next] : body;

        // Doc comments out, for the same reason: every assertion below names a construct, and this file's
        // prose explains those constructs at length. A guard that can be satisfied by the sentence
        // describing the code instead of the code is not a guard.
        method = string.Join('\n', method.Split('\n').Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));

        Assert.True(method.Length > 200, $"the slice from '{signature}' is {method.Length} chars of code — not a method body");
        return method;
    }

    private static string ServiceSourcePath(string fileName)
    {
        var path = Path.Combine(AppProjectDir(), "Services", fileName);
        Assert.True(File.Exists(path), $"{fileName} not found at {path}");
        return path;
    }

    // Walks up to the app project — source is not copied to the test output.
    private static string AppProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "Services")))
            dir = dir.Parent;

        Assert.NotNull(dir);   // else every assertion above would silently test nothing
        return Path.Combine(dir!.FullName, "SysManager");
    }
}
