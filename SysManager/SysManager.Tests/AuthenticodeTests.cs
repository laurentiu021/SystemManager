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
    /// The one chain policy this app uses is the strict one, and it fails closed.
    /// </summary>
    /// <remarks>
    /// Asserted against the source rather than by execution, deliberately and with the reason stated:
    /// producing a signed binary whose chain can be made to fail on demand needs a test certificate and
    /// signtool. What CAN be checked mechanically is that the policy is the strict one — revocation
    /// honoured, root excluded from it, no verification flags relaxed — and that a failed build returns
    /// false rather than falling through.
    /// <para>This guard replaces the equivalent assertions that used to live inside
    /// <c>UpdateServiceAuthenticodeTests.VerifyAuthenticode_PinsThePublisherAndBuildsAChain</c>, which went
    /// RED the moment the chain build moved out of that method — correctly, and it is the reason this one
    /// exists rather than the assertions simply being dropped.</para>
    /// </remarks>
    [Fact]
    public void ValidateChain_UsesTheStrictPolicy_AndFailsClosed()
    {
        var method = HelperMethodSource("internal static bool ValidateChain");

        Assert.Contains("chain.ChainPolicy.RevocationMode = revocation", method, StringComparison.Ordinal);
        Assert.Contains("X509RevocationFlag.ExcludeRoot", method, StringComparison.Ordinal);
        Assert.Contains("X509VerificationFlags.NoFlag", method, StringComparison.Ordinal);
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

        // To the end of the file: ValidateChain is the last member, and a slice that ran to a marker the
        // file no longer contains would silently be empty.
        var method = source[start..];
        Assert.True(method.Length > 200, $"the slice from '{signature}' is {method.Length} chars — not a method body");
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
