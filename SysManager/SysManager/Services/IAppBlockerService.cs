// SysManager · IAppBlockerService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Blocks/unblocks applications via the Image File Execution Options (IFEO)
/// registry mechanism. Extracted as an interface so the registry-mutating logic
/// can be unit-tested against a redirectable registry root instead of writing to
/// the machine's real HKLM hive (Gate-ARCH: system-mutating services are testable).
/// </summary>
public interface IAppBlockerService
{
    /// <summary>Blocks an executable from running. Returns true on success.</summary>
    bool BlockApp(string exeName);

    /// <summary>Unblocks an executable, allowing it to run again. Returns true on success.</summary>
    bool UnblockApp(string exeName);

    /// <summary>Checks whether an executable is currently blocked by SysManager.</summary>
    bool IsBlocked(string exeName);

    /// <summary>Gets all applications currently blocked by SysManager.</summary>
    IReadOnlyList<BlockedApp> GetBlockedApps();
}
