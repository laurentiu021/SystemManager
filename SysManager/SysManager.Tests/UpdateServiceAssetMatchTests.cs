// SysManager · UpdateServiceAssetMatchTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Regression: the release asset matcher must accept the real versioned exe name
/// (SysManager-v&lt;version&gt;.exe) and reject the .sha256 companion. Previously it
/// looked for a fixed "SysManager.exe" that no release ever published, so the
/// in-app updater could never resolve its download asset.
/// </summary>
public class UpdateServiceAssetMatchTests
{
    [Theory]
    [InlineData("SysManager-v1.20.1.exe")]
    [InlineData("SysManager-v1.7.0.exe")]
    [InlineData("SysManager-v2.0.0.exe")]
    [InlineData("sysmanager-v1.20.1.EXE")] // case-insensitive
    public void IsMainExeAsset_AcceptsVersionedExe(string name)
        => Assert.True(UpdateService.IsMainExeAsset(name));

    [Theory]
    [InlineData("SysManager-v1.20.1.exe.sha256")] // checksum companion
    [InlineData("SysManager.exe")]                 // the old fixed name no release uses
    [InlineData("something-else.exe")]
    [InlineData("SysManager-v1.20.1.zip")]
    [InlineData("")]
    [InlineData(null)]
    public void IsMainExeAsset_RejectsNonMatching(string? name)
        => Assert.False(UpdateService.IsMainExeAsset(name));
}
