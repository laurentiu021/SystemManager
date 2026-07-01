// SysManager · UpdateServiceAuthenticodeTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
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
}
