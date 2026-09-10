// SysManager · SignatureVerdict
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Security.Cryptography.X509Certificates;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Helpers;

/// <summary>
/// Turns a file path into the three-state answer an informational column shows: who signed it, or why
/// nobody can say.
/// </summary>
/// <remarks>
/// This is deliberately NOT part of <see cref="Authenticode"/>. That type's contract is that it answers
/// only the mechanical questions — read the signer, build the chain — and holds no policy about what an
/// answer MEANS, because the two fail-closed gates disagree with each other on the most important case:
/// an unsigned file is fatal for the third-party Ookla binary and expected for our own build. Folding a
/// meaning into <c>Authenticode</c> is how one of those gates would quietly become permissive.
/// <para>So this type carries one specific policy: <b>the informational one</b>, used by columns that
/// describe many files and admit no code. Its three rules are the ones a list needs and a gate must not
/// have — unsigned is ordinary rather than fatal, revocation is checked offline against what is already on
/// the machine, and every answer comes with a sentence a non-technical reader can act on. A gate that
/// adopted these would stop being a gate.</para>
/// <para>Shared between the Startup Manager and the Process Manager because both ask the identical
/// question of an executable on disk, and because the alternative — the same paragraph of copy written
/// twice — is the kind of duplication that drifts into two tabs describing the same verdict differently.
/// </para>
/// </remarks>
internal static class SignatureVerdict
{
    /// <summary>
    /// The trust state of the file at <paramref name="path"/>, and a sentence explaining it.
    /// </summary>
    /// <remarks>
    /// Never throws and never returns <see cref="SignatureTrust.Unknown"/>: a caller that could not resolve
    /// a path does not call this at all, which is what keeps "we did not look" distinguishable from "we
    /// looked and this is what we found".
    /// </remarks>
    internal static (SignatureTrust Trust, string Detail) Describe(string path)
    {
        var (state, cert, hresult) = Authenticode.ReadSigner(path);

        if (state is SignerState.Unsigned)
            return (SignatureTrust.Unsigned,
                "Nobody signed this file, so Windows cannot confirm who made it. That is normal for many "
                + "small programs and does not mean it is unsafe — it just means there is nothing to check.");

        if (state is SignerState.Unreadable || cert is null)
            return (SignatureTrust.Invalid,
                $"This file carries signature data that Windows could not read (0x{hresult:X8}). A signed "
                + "file whose signature will not open is worth a closer look.");

        using (cert)
        {
            var signer = CommonName(cert.Subject);

            if (!Authenticode.ValidateChain(cert, X509RevocationMode.Offline, out var statuses))
            {
                Log.Debug("Signature chain did not validate for {Path}: {Status}",
                    LogService.SanitizePath(path), statuses);
                return (SignatureTrust.Invalid,
                    $"This file says it comes from {signer}, but Windows could not confirm that "
                    + $"({statuses}). Worth a closer look before you trust it.");
            }

            return (SignatureTrust.Verified,
                $"Windows can confirm this really comes from {signer}.");
        }
    }

    /// <summary>
    /// The common name out of a certificate subject, or the whole subject when it carries no CN.
    /// </summary>
    /// <remarks>
    /// A subject reads <c>CN=Google LLC, O=Google LLC, L=Mountain View, S=California, C=US</c>. The tooltip
    /// says "comes from Google LLC", so only the CN belongs in it — the rest is correct and unreadable.
    /// A quoted CN containing a comma keeps everything up to the closing quote.
    /// </remarks>
    internal static string CommonName(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "";

        const string marker = "CN=";
        var at = subject.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return subject.Trim();

        var rest = subject[(at + marker.Length)..].TrimStart();
        if (rest.StartsWith('"'))
        {
            var close = rest.IndexOf('"', 1);
            return close > 1 ? rest[1..close] : rest[1..].Trim();
        }

        var comma = rest.IndexOf(',');
        return (comma >= 0 ? rest[..comma] : rest).Trim();
    }
}
