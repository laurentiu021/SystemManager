// SysManager · UpdateServiceAuthenticodeTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Regression tests for <see cref="UpdateService.VerifyAuthenticode"/>.
///
/// The bug: <c>X509Certificate.CreateFromSignedFile</c> THROWS
/// <c>CryptographicException</c> (HResult 0x80092009, CRYPT_E_NO_MATCH) on a file
/// with no embedded Authenticode signature — it does not return null. The old code
/// only returned <c>true</c> from the null branch (which is unreachable) and turned
/// every unsigned file into <c>false</c> via the catch. Since SysManager ships
/// unsigned builds, that made the in-app updater abort EVERY install with
/// "invalid digital signature — possible tampering". These tests pin that an
/// unsigned file is now accepted (true), so the update flow is no longer blocked.
///
/// (File integrity is enforced separately by the SHA256 check, which runs first.)
/// </summary>
public class UpdateServiceAuthenticodeTests
{
    private static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "sysmgr_authtest_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void VerifyAuthenticode_UnsignedFile_ReturnsTrue()
    {
        // A plain file has no embedded Authenticode signature → CreateFromSignedFile
        // throws CRYPT_E_NO_MATCH. This MUST be treated as "unsigned, allowed", not tampering.
        var path = WriteTempFile("not a signed PE, just bytes"u8.ToArray());
        try
        {
            Assert.True(UpdateService.VerifyAuthenticode(path),
                "An unsigned file must be accepted — SysManager ships unsigned builds and the " +
                "SHA256 check is the integrity gate; rejecting here blocks every in-app update.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyAuthenticode_TinyUnsignedFile_ReturnsTrue()
    {
        // A 1-byte file still reports "no signature" (CRYPT_E_NO_MATCH) → allowed.
        var path = WriteTempFile([0x41]);
        try
        {
            Assert.True(UpdateService.VerifyAuthenticode(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyAuthenticode_EmptyFile_ReturnsFalse()
    {
        // An empty file cannot be read as a PE at all (surfaces as E_FAIL, not
        // "no signature"), so it is correctly rejected. A real update is never empty —
        // and the SHA256 step rejects a truncated download before this runs — so this
        // only documents that a malformed/unreadable file is not silently accepted.
        var path = WriteTempFile([]);
        try
        {
            Assert.False(UpdateService.VerifyAuthenticode(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyAuthenticode_RandomBinaryContent_ReturnsTrue()
    {
        // Bytes that vaguely resemble a PE header but carry no signature directory.
        var bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        var path = WriteTempFile(bytes);
        try
        {
            Assert.True(UpdateService.VerifyAuthenticode(path));
        }
        finally { File.Delete(path); }
    }

    // ── Publisher pinning ────────────────────────────────────────────────────────────────────
    //
    // Before this, the signed branch logged cert.Subject and returned true — no subject comparison,
    // no chain build. So once SysManager is signed, a binary signed by ANY certificate, including an
    // attacker's own self-issued one, would pass identically to a legitimate build. The dangerous part
    // is the assumption that arrives with the certificate: "we sign now, so the signature check
    // protects us."
    //
    // The correct pattern was already in this codebase for a THIRD-PARTY download —
    // SpeedTestService.VerifyOoklaSignature pins the subject then builds an X509Chain with online
    // revocation, because (its own comment) "subject alone is forgeable". These tests pin that the
    // update path now shares that shape.

    [Fact]
    public void ExpectedSignerSubject_ExistsAsASinglePinPoint()
    {
        // The point is that enabling signing becomes a ONE-LINE change here, not a security redesign
        // under release pressure. If this constant disappears, the pin has gone with it.
        var field = typeof(UpdateService).GetField("ExpectedSignerSubject",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.Equal(typeof(string), field!.FieldType);
        Assert.True(field.IsLiteral, "must be a const so it cannot be reassigned at runtime");
    }

    [Fact]
    public void ExpectedSignerSubject_IsEmptyWhileBuildsAreUnsigned()
    {
        // Empty means "nothing to pin against yet", which keeps the signed branch permissive for
        // exactly as long as that is true. This is a REMINDER rather than a permanent expectation:
        // when a certificate arrives, this is the assertion that fails, and it points at the pinning
        // tests that will then need real signed fixtures.
        var value = (string)typeof(UpdateService)
            .GetField("ExpectedSignerSubject", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

        Assert.Equal("", value);
    }

    [Fact]
    public void VerifyAuthenticode_PinsThePublisherAndBuildsAChain()
    {
        // Asserted against the source rather than by execution, deliberately and with the limitation
        // stated: producing a genuinely signed binary with a controllable publisher needs a test
        // certificate and signtool, which this unit-test project does not have. What CAN be checked
        // mechanically is that the signed branch does both things the Ookla path does — compare
        // against the pin AND validate the chain. Either one alone is bypassable.
        var source = File.ReadAllText(ServiceSourcePath("UpdateService.cs"));
        var start = source.IndexOf("public static bool VerifyAuthenticode", StringComparison.Ordinal);
        Assert.True(start >= 0, "VerifyAuthenticode not found — this test would otherwise assert nothing");
        var end = source.IndexOf("private static void CleanupFile", start, StringComparison.Ordinal);
        Assert.True(end > start, "method boundary not found");
        var method = source[start..end];

        Assert.Contains("ExpectedSignerSubject", method);          // the pin is consulted
        Assert.Contains("X509Chain", method);                      // the chain is built
        Assert.Contains("X509RevocationMode.Online", method);      // revocation is checked
        Assert.Contains("chain.Build(cert)", method);
        Assert.Contains("return false", method);                   // and it fails closed
    }

    [Fact]
    public void VerifyAuthenticode_UnsignedFile_StillAllowed_AfterThePinWasAdded()
    {
        // Regression guard on the LIVE path: SysManager's builds are unsigned today, so the
        // no-signature branch must stay permissive. Tightening the signed branch must not block every
        // current install — every update aborting with "invalid digital signature" is the bug this
        // test file was originally written for.
        var path = WriteTempFile([0x4D, 0x5A, 0x90, 0x00]);
        try
        {
            Assert.True(UpdateService.VerifyAuthenticode(path));
        }
        finally { File.Delete(path); }
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
}
