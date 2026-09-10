// SysManager · Authenticode
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SysManager.Helpers;

/// <summary>Why a file has no usable signer certificate, or that it has one.</summary>
internal enum SignerState
{
    /// <summary>An embedded Authenticode signature was found and its signer certificate read.</summary>
    Signed,

    /// <summary>
    /// No embedded signature at all. Not an error by itself — most small utilities are unsigned, and
    /// SysManager's own builds are too.
    /// </summary>
    Unsigned,

    /// <summary>
    /// Signature data is present but could not be read, or the file is not a readable image at all
    /// (an empty file surfaces here rather than as <see cref="Unsigned"/>). Suspect, not neutral.
    /// </summary>
    Unreadable,
}

/// <summary>
/// The two Authenticode operations this app performs, defined once: reading a file's signer
/// certificate, and validating that certificate's chain under a single policy.
/// </summary>
/// <remarks>
/// Extracted because the chain policy was written out twice — in
/// <c>SpeedTestService.VerifyOoklaSignature</c> for a third-party download and in
/// <c>UpdateService.VerifyAuthenticode</c> for our own installer — and the two copies are the kind that
/// drift silently: a weakened revocation mode in one of them changes a security posture while every test
/// stays green. The Startup Manager needs the same reading for a third purpose, which would have made it
/// three.
/// <para><b>What is deliberately NOT shared: the policy that decides what the answer MEANS.</b> The two
/// existing callers disagree on the most important case — an unsigned file is fatal for the Ookla binary
/// and expected for our own — and they disagree on whether to throw or return false. Folding those
/// together is how a fail-closed gate quietly becomes permissive, so each caller keeps its own decision
/// and this type answers only the mechanical question.</para>
/// <para><b>Why two calls rather than one.</b> Both callers check the certificate's subject BEFORE
/// building the chain, and building a chain with online revocation makes a network request. A single
/// "inspect" call would therefore have to build the chain first, adding a revocation fetch on exactly the
/// path where the subject already failed. Keeping the steps separate preserves both the ordering and the
/// absence of that request.</para>
/// </remarks>
internal static class Authenticode
{
    /// <summary>
    /// <c>CRYPT_E_NO_MATCH</c> — what <see cref="X509Certificate.CreateFromSignedFile"/> throws for a
    /// file with no embedded signature. It does not return null, and treating the exception as a generic
    /// failure is what once made the in-app updater reject every unsigned build it downloaded.
    /// </summary>
    private const int CryptENoMatch = unchecked((int)0x80092009);

    /// <summary>
    /// Reads the signer certificate embedded in <paramref name="filePath"/>. Never throws: the caller
    /// decides what an absent or unreadable signature means.
    /// </summary>
    /// <returns>
    /// The state, the certificate when <see cref="SignerState.Signed"/> (the caller owns and must dispose
    /// it), and the HResult behind an <see cref="SignerState.Unreadable"/> result for logging.
    /// </returns>
    internal static (SignerState State, X509Certificate2? Certificate, int HResult) ReadSigner(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is obsolete — no direct replacement for Authenticode verification
            var signer = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            return (SignerState.Signed, new X509Certificate2(signer), 0);
        }
        catch (CryptographicException ex) when (ex.HResult == CryptENoMatch)
        {
            return (SignerState.Unsigned, null, ex.HResult);
        }
        catch (CryptographicException ex)
        {
            return (SignerState.Unreadable, null, ex.HResult);
        }
    }

    /// <summary>
    /// Builds <paramref name="certificate"/>'s chain to a trusted root and reports whether it validated.
    /// </summary>
    /// <param name="revocation">
    /// <c>Online</c> for the two fail-closed gates, which verify one file at a moment when a network
    /// request is acceptable. Anything scanning many files, or running on the assumption that the machine
    /// may be offline, passes <c>Offline</c> — a local-first app must not make a revocation request per
    /// file and must not hang when there is no network. This one argument settles certificate downloads
    /// too; see <see cref="PolicyFor"/> for why the two cannot be chosen separately.
    /// </param>
    /// <param name="statuses">
    /// The comma-separated chain statuses when validation fails, for the caller's log. Empty on success.
    /// </param>
    /// <remarks>
    /// The subject of a certificate is forgeable — anyone can issue a self-signed certificate carrying any
    /// subject string — so a subject comparison is meaningless without this. Both fail-closed callers
    /// treat a failure here as a rejection.
    /// </remarks>
    internal static bool ValidateChain(X509Certificate2 certificate, X509RevocationMode revocation, out string statuses)
    {
        using var chain = new X509Chain { ChainPolicy = PolicyFor(revocation) };

        if (chain.Build(certificate))
        {
            statuses = "";
            return true;
        }

        statuses = string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()));
        return false;
    }

    /// <summary>
    /// The chain policy for a given revocation mode: strict verification, and network access allowed only
    /// where the caller has already accepted it.
    /// </summary>
    /// <remarks>
    /// <b>Certificate downloads travel with the revocation mode, and this is the reason the method
    /// exists.</b> <see cref="X509RevocationMode.Offline"/> suppresses the CRL and OCSP fetch, and it is
    /// easy to read that as "this chain build does not touch the network". It is not: when an intermediate
    /// certificate is missing from the local store, the chain engine follows the Authority Information
    /// Access extension and downloads it, under a separate switch that defaults to allowing it. So a caller
    /// that chose <c>Offline</c> because it is scanning a whole tab's worth of files still made a request
    /// per unrecognised issuer — and on a machine with no network, waited out
    /// <see cref="X509ChainPolicy.UrlRetrievalTimeout"/> for each one, which is the stall <c>Offline</c>
    /// was picked to avoid.
    /// <para>The two settings are therefore derived from one another rather than exposed as a second
    /// parameter: a caller passing <c>Offline</c> has already said it cannot afford a network request, and
    /// a caller passing <c>Online</c> is one of the two fail-closed gates verifying a single file at a
    /// moment when a request is expected — there, downloading a missing intermediate is what lets a
    /// legitimate signature validate.</para>
    /// <para>The cost on the offline path is that a signed file whose issuer is not cached locally now
    /// fails to validate where it previously might have succeeded after a download. That is the honest
    /// answer for an informational column: the tooltip says Windows could not confirm the publisher, which
    /// is exactly what happened, and it says it immediately instead of after a timeout.</para>
    /// </remarks>
    internal static X509ChainPolicy PolicyFor(X509RevocationMode revocation) => new()
    {
        RevocationMode = revocation,
        RevocationFlag = X509RevocationFlag.ExcludeRoot,
        VerificationFlags = X509VerificationFlags.NoFlag,
        DisableCertificateDownloads = revocation is X509RevocationMode.Offline,
    };
}
