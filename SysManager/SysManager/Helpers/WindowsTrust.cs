// SysManager · WindowsTrust
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
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
/// <para><b>Catalog signatures are read too, and they are not a detail.</b> <c>WTD_CHOICE_FILE</c> sees only
/// the signature embedded in a file, and Windows signs most of its own components through a <c>.cat</c>
/// catalogue instead — 35 of those 82 images carry no embedded signature, and 12 of the 35 are verified
/// through a catalogue, including <c>powershell.exe</c>, <c>cmd.exe</c>, <c>conhost.exe</c> and the search
/// host. Reporting those as unsigned understated them on the one tab whose question is "is this Windows?".
/// The remaining 23 are genuinely unsigned third-party programs.</para>
/// <para>The catalogue lookup is a second question asked only when the first finds nothing, so a file with
/// its own signature pays nothing for it. Both cost roughly 25 ms and neither improves on a warm pass.</para>
/// </remarks>
internal static partial class WindowsTrust
{
    /// <summary><c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c> — the standard Authenticode policy provider.</summary>
    private static Guid _genericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>The hash algorithm the catalogue lookup asks for, by name and never by default.</summary>
    private const string Sha256 = "SHA256";

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdChoiceCatalog = 2;
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
    /// <c>WINTRUST_CATALOG_INFO</c> — the union member for verifying a file as part of a catalogue.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    // CATALOG_INFO is { DWORD cbStruct; WCHAR wszCatalogFile[MAX_PATH]; }. The inline WCHAR array is not
    // marshallable by a source-generated P/Invoke, so it is passed as raw memory and the path read out by
    // hand. The offset is where the array starts: straight after the DWORD, no alignment padding for WCHAR.
    private const int CatalogInfoSize = 4 + 260 * 2;
    private const int CatalogInfoPathOffset = 4;

    /// <summary>
    /// <c>WinVerifyTrust</c> has no A/W variants, so no <c>EntryPoint</c> suffix applies. It reports
    /// failure as a non-zero HRESULT return rather than by setting the last error, so there is no
    /// <c>SetLastError</c> either. The same is true of the catalogue functions below, which report failure
    /// through their return value.
    /// </summary>
    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr window, ref Guid action, IntPtr data);

    [LibraryImport("wintrust.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminAcquireContext2(
        out IntPtr admin, IntPtr subsystem, string hashAlgorithm, IntPtr policy, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminCalcHashFromFileHandle2(
        IntPtr admin, IntPtr file, ref uint hashSize, IntPtr hash, uint flags);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr CryptCATAdminEnumCatalogFromHash(
        IntPtr admin, IntPtr hash, uint hashSize, uint flags, IntPtr previous);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATCatalogInfoFromContext(IntPtr catalog, IntPtr info, uint flags);

    /// <summary>
    /// Windows' verdict on the signature of the file at <paramref name="filePath"/>. Never throws.
    /// </summary>
    /// <remarks>
    /// Two questions, asked in order. First the signature embedded in the file; if there is none, the
    /// Windows catalogues, because that is how Windows signs most of its own components — and 12 of the 35
    /// process images with no embedded signature on a stock Windows 11 machine are verified that way,
    /// including <c>powershell.exe</c>, <c>cmd.exe</c>, <c>conhost.exe</c> and the search host. Reporting
    /// those as unsigned understated them, on the one tab whose question is "is this Windows?".
    /// <para>The catalogue lookup runs ONLY when the embedded check finds nothing, so a file carrying its
    /// own signature pays nothing for it. Both steps cost roughly 25 ms and neither gets cheaper on a
    /// second pass, so a caller covering a whole list needs a cache and must stay off the UI thread.</para>
    /// </remarks>
    internal static TrustResult Verify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return TrustResult.NoSignature;

        var embedded = VerifyEmbedded(filePath);

        // Only "no signature in the file" is worth a second question. An expired or untrusted embedded
        // signature is an answer, and going looking for a catalogue that might say otherwise would be
        // choosing the more flattering of two verdicts.
        return embedded is TrustResult.NoSignature ? VerifyByCatalog(filePath) : embedded;
    }

    /// <summary>The verdict on the signature embedded in the file itself.</summary>
    private static TrustResult VerifyEmbedded(string filePath)
    {
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

    /// <summary>
    /// The verdict from a Windows catalogue, for a file that carries no signature of its own.
    /// </summary>
    /// <remarks>
    /// Four native steps: take a catalogue-admin context, hash the file the way the catalogues do, find the
    /// catalogue holding that hash, then verify the file AS A MEMBER of it. The hash is both the lookup key
    /// and the member tag, which is why it is computed once and passed to both.
    /// <para><b>SHA-256, explicitly.</b> <c>CryptCATAdminAcquireContext2</c> takes the algorithm by name;
    /// the older <c>CryptCATAdminAcquireContext</c> implies SHA-1 and has no place in new code.</para>
    /// <para><b>Every handle is released on every path, including the failures</b>, and this is the
    /// leak-prone part: there are two native handles and a native allocation, and three of the five steps
    /// can fail. A leak here is once per unsigned file per refresh, on a tab that refreshes on a timer.</para>
    /// <para>A file the app cannot open — locked, or access denied — is reported as unsigned rather than as
    /// a problem, matching what the embedded check does for anything it cannot parse. "We could not look"
    /// must not read as "we looked and it failed".</para>
    /// </remarks>
    private static TrustResult VerifyByCatalog(string filePath)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, Sha256, IntPtr.Zero, 0))
            return TrustResult.NoSignature;

        FileStream? file = null;
        var hash = IntPtr.Zero;
        var catalog = IntPtr.Zero;
        var info = IntPtr.Zero;

        try
        {
            try { file = File.OpenRead(filePath); }
            catch (IOException) { return TrustResult.NoSignature; }
            catch (UnauthorizedAccessException) { return TrustResult.NoSignature; }
            catch (ArgumentException) { return TrustResult.NoSignature; }

            var handle = file.SafeFileHandle.DangerousGetHandle();

            // First call sizes the hash, second fills it — the documented two-step.
            uint size = 0;
            CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, IntPtr.Zero, 0);
            if (size == 0) return TrustResult.NoSignature;

            hash = Marshal.AllocHGlobal((int)size);
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, hash, 0))
                return TrustResult.NoSignature;

            catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
            if (catalog == IntPtr.Zero) return TrustResult.NoSignature;   // genuinely in no catalogue

            info = Marshal.AllocHGlobal(CatalogInfoSize);
            Marshal.Copy(new byte[CatalogInfoSize], 0, info, CatalogInfoSize);
            Marshal.WriteInt32(info, 0, CatalogInfoSize);

            if (!CryptCATCatalogInfoFromContext(catalog, info, 0)) return TrustResult.NoSignature;

            var catalogPath = Marshal.PtrToStringUni(info + CatalogInfoPathOffset);
            if (string.IsNullOrEmpty(catalogPath)) return TrustResult.NoSignature;

            return VerifyCatalogMember(catalogPath, HexTag(hash, size), filePath, hash, size, admin);
        }
        finally
        {
            if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
            if (catalog != IntPtr.Zero) CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
            if (hash != IntPtr.Zero) Marshal.FreeHGlobal(hash);
            file?.Dispose();
            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    /// <summary>Verifies <paramref name="member"/> as a member of <paramref name="catalogPath"/>.</summary>
    private static TrustResult VerifyCatalogMember(
        string catalogPath, string memberTag, string member, IntPtr hash, uint hashSize, IntPtr admin)
    {
        var pCatalog = Marshal.StringToHGlobalUni(catalogPath);
        var pTag = Marshal.StringToHGlobalUni(memberTag);
        var pMember = Marshal.StringToHGlobalUni(member);
        var catalogInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
        var data = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());

        try
        {
            Marshal.StructureToPtr(
                new WINTRUST_CATALOG_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                    pcwszCatalogFilePath = pCatalog,
                    pcwszMemberTag = pTag,
                    pcwszMemberFilePath = pMember,
                    pbCalculatedFileHash = hash,
                    cbCalculatedFileHash = hashSize,
                    hCatAdmin = admin,
                },
                catalogInfo, fDeleteOld: false);

            Marshal.StructureToPtr(
                new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceCatalog,
                    pFile = catalogInfo,
                    dwStateAction = WtdStateActionVerify,
                    dwProvFlags = WtdSaferFlag | WtdCacheOnlyUrlRetrieval | WtdRevocationCheckNone,
                },
                data, fDeleteOld: false);

            var hr = WinVerifyTrust(IntPtr.Zero, ref _genericVerifyV2, data);
            Close(data);

            return Classify(hr);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(catalogInfo);
            Marshal.FreeHGlobal(pMember);
            Marshal.FreeHGlobal(pTag);
            Marshal.FreeHGlobal(pCatalog);
        }
    }

    /// <summary>
    /// The file hash as the upper-case hex string used for the catalogue member tag.
    /// </summary>
    /// <remarks>
    /// Upper case is the conventional form and what the documentation shows. It is <b>not</b> load-bearing,
    /// and that was measured rather than assumed: forcing the tag to lower case leaves every real catalogue
    /// verification passing, so the trust provider is not matching on this string the way one would expect
    /// from its name. Kept upper-case for consistency with the documented shape, and said plainly here so
    /// nobody defends it as a correctness requirement.
    /// </remarks>
    internal static string HexTag(IntPtr hash, uint size)
    {
        var bytes = new byte[size];
        Marshal.Copy(hash, bytes, 0, (int)size);
        return Convert.ToHexString(bytes);
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
