// SysManager · WindowsFeatureScriptTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// <c>WindowsFeaturesService.ChangeScript</c> and <c>QueryScript</c> run in real Windows PowerShell, with the DISM
/// cmdlets shadowed by functions so no feature on this machine is read or changed.
/// </summary>
/// <remarks>
/// What the change script prints when the cmdlet refuses only shows in PowerShell itself: the code is the exception's
/// <c>HResult</c>, negative for every DISM error and formatted in hex by PowerShell, and DISM's message runs over
/// several lines, of which the first is the reason. The catch printed nothing, so every failure on the tab said "Check
/// permissions" (#2834). <see cref="WindowsFeaturesService.ParseChange"/> then reads the real output, not a copy of it.
/// </remarks>
public class WindowsFeatureScriptTests
{
    // How the cmdlet refuses a feature whose files Windows cannot get: a COMException carrying DISM's code, with the
    // message in two lines, as DISM words it.
    private const string EnableRefuses =
        "function Enable-WindowsOptionalFeature { [CmdletBinding()] " +
        "param([switch]$Online, $FeatureName, [switch]$NoRestart, [switch]$All) " +
        "throw [System.Runtime.InteropServices.COMException]::new(" +
        "\"The source files could not be found.`r`nUse the Source option to specify the location of the files.\", " +
        "-2146498529) } ; ";

    private const string EnableNeedsARestart =
        "function Enable-WindowsOptionalFeature { [CmdletBinding()] " +
        "param([switch]$Online, $FeatureName, [switch]$NoRestart, [switch]$All) " +
        "[PSCustomObject]@{ RestartNeeded = $true } } ; ";

    // The state as the real cmdlet types it, so the test reads the enum's own text rather than a string of its own.
    private const string FeatureWaitsForARestart =
        "Import-Module Dism; " +
        "function Get-WindowsOptionalFeature { [CmdletBinding()] param([switch]$Online, $FeatureName) " +
        "[PSCustomObject]@{ FeatureName = 'TelnetClient'; " +
        "State = [Microsoft.Dism.Commands.FeatureState]::EnablePending } } ; ";

    private static string Lines(string output) => output.Replace("\r", "", StringComparison.Ordinal);

    [Fact]
    public async Task ChangeScript_WhenTheCmdletRefuses_PrintsDismsCodeAndReason_AndExitsOne()
    {
        var (exit, output, _) = await WindowsPowerShellScript.RunAsync(
            EnableRefuses + WindowsPowerShellScript.StubsInEffect("Enable-WindowsOptionalFeature")
            + WindowsFeaturesService.ChangeScript(enable: true, "TelnetClient"));

        Assert.Equal(1, exit);
        Assert.Contains($"{WindowsFeaturesService.FailureMarker}|800F081F|The source files could not be found.",
            output, StringComparison.Ordinal);

        var result = WindowsFeaturesService.ParseChange(exit, Lines(output).Split('\n'));
        Assert.False(result.Success);
        Assert.Equal(unchecked((int)0x800F081Fu), result.ErrorCode);
        Assert.Equal("The source files could not be found.", result.ErrorMessage);
    }

    [Fact]
    public async Task ChangeScript_WhenTheChangeGoesThrough_PrintsTheRestartFlag_AndExitsZero()
    {
        // The positive control: a script that always took the catch would pass the test above.
        var (exit, output, _) = await WindowsPowerShellScript.RunAsync(
            EnableNeedsARestart + WindowsPowerShellScript.StubsInEffect("Enable-WindowsOptionalFeature")
            + WindowsFeaturesService.ChangeScript(enable: true, "TelnetClient"));

        Assert.Equal(0, exit);
        Assert.DoesNotContain(WindowsFeaturesService.FailureMarker, output, StringComparison.Ordinal);
        Assert.Equal(new SysManager.Models.FeatureChangeResult(true, true),
            WindowsFeaturesService.ParseChange(exit, Lines(output).Split('\n')));
    }

    [Fact]
    public async Task QueryScript_AFeatureWaitingForARestart_ReadsAsOn()
    {
        var (exit, output, _) = await WindowsPowerShellScript.RunAsync(
            FeatureWaitsForARestart + WindowsPowerShellScript.StubsInEffect("Get-WindowsOptionalFeature")
            + WindowsFeaturesService.QueryScript("TelnetClient"));

        Assert.Equal(0, exit);
        var row = Assert.Single(WindowsFeaturesService.ParseFeatureList([.. Lines(output).Split('\n')]));
        Assert.True(row.IsEnabled);
        Assert.True(row.RequiresReboot);
    }
}
