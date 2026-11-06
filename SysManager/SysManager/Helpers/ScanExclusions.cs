// SysManager · ScanExclusions
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Buffers;
using System.Collections.Frozen;

namespace SysManager.Helpers;

/// <summary>
/// What Large Files and Duplicate Finder leave out: the system folders they do not enter, and the files Windows
/// manages itself. Held once, so the two cannot drift apart.
/// </summary>
/// <remarks>
/// Each scanner kept its own two lists and asked them through <c>Any</c> with a lambda, which cost a closure and a
/// delegate for every file and every folder of a scan (#2854). A <see cref="FrozenSet{T}"/> finds a name by its hash,
/// and <see cref="SearchValues{T}"/> looks for every folder in one pass over a path, neither allocating. Disk Analyzer
/// keeps a list of its own, without <c>Windows\System32\config</c>: it reports how the space is used, so it counts
/// what that folder holds.
/// </remarks>
internal static class ScanExclusions
{
    /// <summary>The system folders a scan does not enter, found anywhere in a folder's path, in any case.</summary>
    internal static readonly SearchValues<string> SystemFolders = SearchValues.Create(
        [@"\$recycle.bin", @"\system volume information", @"\windows\winsxs", @"\windows\system32\config",
         @"\windows\csc"],
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Files Windows manages itself, matched by exact name in any case.
    /// </summary>
    /// <remarks>
    /// These are normally the two largest files on the system drive — a hibernation file is a fraction of
    /// installed RAM and a page file several gigabytes — so they took the top of the one list Large Files exists
    /// to produce, and neither of the actions offered beside a row ("Show in Explorer", "Copy path") can do
    /// anything about a file Windows holds open (#2386). Disk Analyzer still counts them, which is where that space
    /// is meant to be accounted for; these are lists of files a user might act on.
    /// <para>Exact name, not a substring as the folder list uses: <c>my-pagefile.sys.bak</c> is the user's own file
    /// and contains one of these names. Not part of <see cref="SystemFolders"/> either: that list is asked only about
    /// a folder the walk is about to enter, so the three names used to sit in Large Files' copy of it and never be
    /// consulted.</para>
    /// </remarks>
    internal static readonly FrozenSet<string> WindowsManagedFiles =
        new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
