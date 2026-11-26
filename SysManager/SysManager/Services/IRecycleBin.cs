// SysManager · IRecycleBin
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// The current user's Recycle Bin, measured and emptied through the shell.
/// </summary>
/// <remarks>
/// Behind a seam because emptying it for real from a test empties the bin of the PC running the tests. Quick Cleanup,
/// Deep Cleanup and the Quick Tune-Up all reach the bin through it, and so does Quick Cleanup's size label, so what each
/// of them reports and records about the bin can be tested against a substitute.
/// </remarks>
public interface IRecycleBin
{
    /// <summary>What the bin holds on every drive, as the shell counts it, or null when the shell cannot say.</summary>
    /// <remarks>Reads every drive, so callers keep it off the UI thread.</remarks>
    RecycleBinContents? Query();

    /// <summary>Empties the bin on every drive, without asking, and reports what it held just before.</summary>
    /// <remarks>Can take a long time on a full bin. Callers confirm first and keep it off the UI thread.</remarks>
    RecycleBinEmptyResult Empty();

    /// <summary>
    /// True when the bin the shell would measure and empty may not be the signed-in person's: SysManager runs as
    /// administrator, and the account of its session is not positively the one it runs as (#3316).
    /// </summary>
    bool BelongsToAnotherAccount => false;
}

/// <summary>The real Recycle Bin, through <see cref="RecycleBinHelper"/>.</summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    private readonly Func<bool> _isTheSignedInAccounts;
    private readonly Func<RecycleBinContents?> _query;
    private readonly Func<RecycleBinEmptyResult> _empty;

    /// <summary>The bin of the account SysManager runs as, refused when that may not be the signed-in one.</summary>
    public ShellRecycleBin()
        : this(AdminHelper.RunsAsTheSignedInAccount, RecycleBinHelper.Query, RecycleBinHelper.Empty)
    {
    }

    /// <summary>
    /// A bin over the shell calls given, so a test can be either account without measuring or emptying the bin of the
    /// PC running it.
    /// </summary>
    internal ShellRecycleBin(Func<bool> isTheSignedInAccounts, Func<RecycleBinContents?> query,
                             Func<RecycleBinEmptyResult> empty)
    {
        _isTheSignedInAccounts = isTheSignedInAccounts;
        _query = query;
        _empty = empty;
    }

    /// <inheritdoc/>
    public bool BelongsToAnotherAccount => !_isTheSignedInAccounts();

    /// <inheritdoc/>
    public RecycleBinContents? Query() => _query();

    /// <inheritdoc/>
    public RecycleBinEmptyResult Empty() => _empty();
}
