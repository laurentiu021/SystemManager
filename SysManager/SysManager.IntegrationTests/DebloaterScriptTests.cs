// SysManager · DebloaterScriptTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// <c>DebloaterService.ListScript</c> run in real Windows PowerShell, with <c>Get-AppxPackage</c> shadowed by a
/// function so nothing on this machine is read or changed.
/// </summary>
/// <remarks>
/// What the script must tell apart only shows in PowerShell itself: a non-terminating error, which is how
/// <c>Get-AppxPackage</c> reports a failure, does not stop a script unless the script makes it. A read that
/// failed has to stop the script, so the runner throws and the tab says it could not read the apps (#2487).
/// </remarks>
public class DebloaterScriptTests
{
    private static readonly string Guard = WindowsPowerShellScript.StubsInEffect("Get-AppxPackage");

    // Printed after the list, so it shows whether a failed read STOPPED the script. Stopping is what makes the
    // runner throw, and the throw is how ListAsync tells a failed read from an empty one.
    private const string AfterList = "; '__SM_AFTER_LIST__'";

    // One package, shaped like the fields the script selects.
    private const string OnePackage =
        "[PSCustomObject]@{ Name = 'Contoso.AppA'; PackageFullName = 'Contoso.AppA_1.0.0.0_x64__8wekyb3d8bbwe'; " +
        "PackageFamilyName = 'Contoso.AppA_8wekyb3d8bbwe'; Publisher = 'CN=Contoso'; Version = '1.0.0.0'; " +
        "IsFramework = $false; IsResourcePackage = $false }";

    [Fact]
    public async Task WhenTheReadFailsOutright_StopsTheScript()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            "function Get-AppxPackage { [CmdletBinding()] param() " +
            "Write-Error -Message 'The AppX deployment service did not answer.' -Category ResourceUnavailable } ; " +
            Guard + DebloaterService.ListScript + AfterList);

        Assert.DoesNotContain("__SM_AFTER_LIST__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenOnePackageFails_KeepsTheOthers()
    {
        // Why the script does not simply use -ErrorAction Stop: one damaged package would then lose the whole tab.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            "function Get-AppxPackage { [CmdletBinding()] param() " + OnePackage + " ; " +
            "Write-Error -Message 'The package manifest is damaged.' -Category InvalidData } ; " +
            Guard + DebloaterService.ListScript + AfterList);

        Assert.Contains("Contoso.AppA", output, StringComparison.Ordinal);
        Assert.Contains("__SM_AFTER_LIST__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenWindowsHasNone_CarriesOn()
    {
        // The positive control: an empty answer is not a failure. A script that stopped on everything would pass
        // the failure case above.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            "function Get-AppxPackage { [CmdletBinding()] param() } ; " +
            Guard + DebloaterService.ListScript + AfterList);

        Assert.Contains("__SM_AFTER_LIST__", output, StringComparison.Ordinal);
    }
}
