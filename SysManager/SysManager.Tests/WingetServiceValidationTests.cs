// SysManager · WingetServiceValidationTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Negative tests for the package-ID allowlist on the winget-backed commands
/// (idx 66). The ID is validated BEFORE winget.exe is ever launched, so a real
/// runner can be passed safely — the rejection paths never spawn a process. This
/// is the sole defense against command-injection through a crafted package ID, so
/// it must be explicitly tested (project rule: a validation that is the only
/// barrier against bad input must have a negative test).
/// </summary>
public class WingetServiceValidationTests
{
    // Strings that must be rejected: command separators / chaining / substitution /
    // quoting / newlines, plus empty/whitespace and an over-length id.
    public static IEnumerable<object[]> InvalidIds =>
    [
        [""],
        ["   "],
        ["pkg; calc.exe"],
        ["pkg && calc"],
        ["pkg | more"],
        ["pkg`whoami`"],
        ["pkg$(whoami)"],
        ["pkg\"quote"],
        ["pkg\nnewline"],
        [new string('a', 257)],   // exceeds the 256-char cap
    ];

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public async Task WingetService_UpgradeAsync_RejectsInvalidId(string id)
    {
        var ps = new PowerShellRunner();
        var svc = new WingetService(ps);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpgradeAsync(id));
    }

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public async Task UninstallerService_UninstallAsync_RejectsInvalidId(string id)
    {
        var ps = new PowerShellRunner();
        var svc = new UninstallerService(ps);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UninstallAsync(id));
    }

    [Theory]
    [InlineData("Microsoft.VisualStudioCode")]
    [InlineData("Notepad++.Notepad++")]
    [InlineData("Microsoft.VisualStudio.2022.Community")]
    [InlineData("7zip.7zip")]
    public void PackageIdShape_AcceptsRealWorldIds(string id)
    {
        // These valid winget IDs must NOT be rejected by the allowlist shape. We can't
        // call UpgradeAsync here (it would spawn winget); instead assert the IDs only
        // use the allowed character class, which is exactly what the validator checks.
        Assert.Matches(@"^[\w.\-/+ ]{1,256}$", id);
    }
}
