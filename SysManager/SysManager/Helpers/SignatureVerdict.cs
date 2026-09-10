// SysManager · SignatureVerdict
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Helpers;

/// <summary>
/// Turns a file path into the three-state answer an informational column shows: who signed it, or why
/// nobody can say.
/// </summary>
/// <remarks>
/// <b>The verdict comes from Windows, not from a chain we build ourselves.</b> This used to read the
/// signer certificate and build an <c>X509Chain</c> offline, and that does not work: measured over the 82
/// distinct process images on a stock Windows 11 machine it verified <b>1</b> and reported "Check failed"
/// for <b>47</b>, including Explorer, svchost and Outlook. An offline chain build still demands a
/// revocation answer it has no cached CRL for, and an intermediate certificate it cannot fetch. See
/// <see cref="WindowsTrust"/> for the measurement and the flags. <c>WinVerifyTrust</c> over the same files
/// returns 46 trusted and 1 genuine failure.
/// <para>The certificate is still read, but only for the publisher's <em>name</em> — never for the
/// verdict. That ordering matters: a name is cosmetic, so failing to read one costs a phrase in a tooltip,
/// whereas the verdict decides what colour the user sees.</para>
/// <para>This is deliberately NOT part of <see cref="Authenticode"/>. That type answers only the
/// mechanical managed questions and holds no policy, because the two fail-closed gates disagree with each
/// other on the most important case: an unsigned file is fatal for the third-party Ookla binary and
/// expected for our own build. Folding a meaning into it is how one of those gates would quietly become
/// permissive.</para>
/// <para>So this type carries one specific policy: <b>the informational one</b>, used by columns that
/// describe many files and admit no code. Its rules are the ones a list needs and a gate must not have —
/// unsigned is ordinary rather than fatal, nothing reaches the network, and every answer comes with a
/// sentence a non-technical reader can act on. A gate that adopted these would stop being a gate.</para>
/// <para>Shared between the Startup Manager and the Process Manager because both ask the identical
/// question of an executable on disk, and because the alternative — the same paragraph of copy written
/// twice — is the kind of duplication that drifts into two tabs describing one verdict differently.</para>
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
        var verdict = WindowsTrust.Verify(path);

        return verdict switch
        {
            TrustResult.NoSignature => (SignatureTrust.Unsigned,
                "Nobody signed this file, so Windows cannot confirm who made it. That is normal for many "
                + "small programs and does not mean it is unsafe — it just means there is nothing to check."),

            TrustResult.Expired => (SignatureTrust.Invalid,
                Signer(path) is { Length: > 0 } expired
                    ? $"This file says it comes from {expired}, and the certificate used to sign it has "
                      + "expired without a timestamp Windows can fall back on. Common in older programs, "
                      + "and not proof of anything wrong — but it means the signature can no longer be "
                      + "confirmed."
                    : "The certificate used to sign this file has expired without a timestamp Windows can "
                      + "fall back on, so the signature can no longer be confirmed."),

            TrustResult.NotTrusted => (SignatureTrust.Invalid,
                Signer(path) is { Length: > 0 } claimed
                    ? $"This file says it comes from {claimed}, but Windows does not trust that signature. "
                      + "Worth a closer look before you trust it."
                    : "This file carries a signature Windows does not trust. Worth a closer look before "
                      + "you trust it."),

            _ => (SignatureTrust.Verified,
                Signer(path) is { Length: > 0 } signer
                    ? $"Windows can confirm this really comes from {signer}."
                    : "Windows can confirm this file is signed and has not been modified since."),
        };
    }

    /// <summary>
    /// The publisher name from the file's embedded certificate, or an empty string when there is none to
    /// read.
    /// </summary>
    /// <remarks>
    /// Cosmetic by design. An empty result costs the tooltip a name and changes no verdict, which is why
    /// this is a separate step rather than folded into the trust check — every phrasing in
    /// <see cref="Describe"/> has a form that works without it.
    /// </remarks>
    private static string Signer(string path)
    {
        var (state, cert, _) = Authenticode.ReadSigner(path);
        if (state is not SignerState.Signed || cert is null) return "";

        using (cert)
        {
            return CommonName(cert.Subject);
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
