// SysManager · WindowsTrust
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;

namespace SysManager.Helpers;

/// <summary>What Windows itself concludes about a file's signature.</summary>
internal enum TrustResult
{
    /// <summary>Signed, and Windows trusts the signature: the chain resolves and the file is unmodified.</summary>
    Trusted,

    /// <summary>No signature Windows can see. Ordinary — most small programs are unsigned, and so are ours.</summary>
    NoSignature,

    /// <summary>The signing certificate has expired and no countersignature timestamp rescues it.</summary>
    Expired,

    /// <summary>
    /// Signed, and the signature does not hold up: an untrusted or unreachable root, a revoked or
    /// explicitly distrusted certificate, or a file altered after it was signed. The one state worth a
    /// second look.
    /// </summary>
    NotTrusted,
}

/// <summary>
/// Asks Windows whether it trusts a file's signature, through <c>WinVerifyTrust</c>.
/// </summary>
/// <remarks>
/// <b>Why this exists alongside <see cref="Authenticode"/> rather than inside it.</b> Those are two
/// different mechanisms answering two different questions, and conflating them is what produced the defect
/// this type fixes. <see cref="Authenticode"/> reads a certificate and builds a chain with the managed
/// <c>X509Chain</c>, which the two fail-closed gates need because they compare a specific publisher's
/// subject before deciding. This asks the operating system for a verdict, which is what an informational
/// column needs.
/// <para><b>The measurement that made the case.</b> Over the 82 distinct process images running on a stock
/// Windows 11 machine, the managed approach verified <b>1</b> and reported "Check failed" for <b>47</b> —
/// including Explorer, svchost and Outlook. <c>WinVerifyTrust</c> over the same files: <b>46 trusted, 1
/// genuine failure</b> (an expired certificate). The managed path cannot do better without the network,
/// because <c>RevocationMode.Offline</c> still demands a revocation answer it has no cached CRL for
/// (<c>RevocationStatusUnknown</c> on 46 of 48) and a chain it cannot complete locally
/// (<c>PartialChain</c> on 29). Loosening the flags far enough to pass would mean accepting any
/// certificate authority, which is not verification.</para>
/// <para><b>No network request.</b> <c>WTD_REVOKE_NONE</c> asks for no revocation check, and
/// <c>WTD_CACHE_ONLY_URL_RETRIEVAL</c> tells the trust provider to answer from this machine's caches
/// rather than fetching anything. That is the "local only" guarantee the offline chain policy was reached
/// for and does not actually deliver — here it is a documented, supported mode rather than a side effect
/// of a revocation setting.</para>
/// <para><b>What this does NOT see: catalog signatures.</b> <c>WTD_CHOICE_FILE</c> verifies the signature
/// embedded in the file, and Windows components are largely signed through a <c>.cat</c> catalog instead.
/// 35 of the 82 images above fall in that group and still report <see cref="TrustResult.NoSignature"/>.
/// Tracked separately; adding it means a catalog lookup before this call, not a change to it.</para>
/// </remarks>
internal static partial class WindowsTrust
{
    /// <summary><c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c> — the standard Authenticode policy provider.</summary>
    private static Guid _genericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdSaferFlag = 0x100;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    private const uint WtdRevocationCheckNone = 0x10;

    // The HRESULTs worth telling apart. Everything else is folded into NotTrusted rather than guessed at.
    private const uint TrustENoSignature = 0x800B0100;
    private const uint TrustESubjectFormUnknown = 0x800B0003;
    private const uint TrustEProviderUnknown = 0x800B0001;
    private const uint CertEExpired = 0x800B0101;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    /// <summary>
    /// <c>WinVerifyTrust</c> has no A/W variants, so no <c>EntryPoint</c> suffix applies. It reports
    /// failure as a non-zero HRESULT return rather than by setting the last error, so there is no
    /// <c>SetLastError</c> either.
    /// </summary>
    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr window, ref Guid action, IntPtr data);

    /// <summary>
    /// Windows' verdict on the signature of the file at <paramref name="filePath"/>. Never throws.
    /// </summary>
    /// <remarks>
    /// Costs roughly 25 ms per file and does not get cheaper on a second pass, so a caller covering a
    /// whole list needs a cache and must keep this off the UI thread.
    /// </remarks>
    internal static TrustResult Verify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return TrustResult.NoSignature;

        var path = Marshal.StringToHGlobalUni(filePath);
        var file = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        var data = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());

        try
        {
            Marshal.StructureToPtr(
                new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = path,
                },
                file, fDeleteOld: false);

            Marshal.StructureToPtr(
                new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = file,
                    dwStateAction = WtdStateActionVerify,
                    dwProvFlags = WtdSaferFlag | WtdCacheOnlyUrlRetrieval | WtdRevocationCheckNone,
                },
                data, fDeleteOld: false);

            var hr = WinVerifyTrust(IntPtr.Zero, ref _genericVerifyV2, data);

            // WTD_STATEACTION_VERIFY allocates state inside the trust provider; the CLOSE call is what
            // releases it. Skipping it leaks native memory once per file, which on a list this size is the
            // difference between a tab that costs nothing and one that grows all session.
            Close(data);

            return Classify(hr);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(file);
            Marshal.FreeHGlobal(path);
        }
    }

    /// <summary>Releases the provider state the verify call allocated.</summary>
    private static void Close(IntPtr data)
    {
        var closing = Marshal.PtrToStructure<WINTRUST_DATA>(data);
        closing.dwStateAction = WtdStateActionClose;
        Marshal.StructureToPtr(closing, data, fDeleteOld: false);
        WinVerifyTrust(IntPtr.Zero, ref _genericVerifyV2, data);
    }

    /// <summary>
    /// Maps an HRESULT to the four states. Pure and internal so the mapping is testable without a signed
    /// file — producing one on demand needs a certificate and signtool, and the mapping is the part that
    /// decides what the user is told.
    /// </summary>
    /// <remarks>
    /// The three "no signature" HRESULTs are grouped deliberately. <c>TRUST_E_NOSIGNATURE</c> is the
    /// ordinary one; <c>TRUST_E_SUBJECT_FORM_UNKNOWN</c> and <c>TRUST_E_PROVIDER_UNKNOWN</c> come back for
    /// a file the Authenticode provider does not handle at all, which is also "there is nothing here to
    /// check" rather than "this is suspect". Calling those NotTrusted would put an amber chip on ordinary
    /// files and teach the user to ignore the column, which is the failure mode this whole change exists
    /// to undo.
    /// </remarks>
    internal static TrustResult Classify(int hresult) => (uint)hresult switch
    {
        0 => TrustResult.Trusted,
        TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => TrustResult.NoSignature,
        CertEExpired => TrustResult.Expired,
        _ => TrustResult.NotTrusted,
    };
}
