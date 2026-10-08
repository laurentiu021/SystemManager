// SysManager · DeliveryOptimizationCommandTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// The command Deep Cleanup empties the Delivery Optimization cache with is a real Windows PowerShell command, by the
/// name and with the switch its script uses (#2602).
/// </summary>
/// <remarks>
/// Read only: the command is looked up, never run, so nothing in the cache of the machine running this changes. The
/// unit tests take the runner's word for what the command does; this is the half they cannot show, that a typo in the
/// name or the switch would not reach a user as a clean that fails every time.
/// </remarks>
public class DeliveryOptimizationCommandTests
{
    [Fact]
    public async Task TheCommandTheScriptRuns_IsWindowsOwn_AndTakesForce()
    {
        var name = DeepCleanupService.EmptyDeliveryOptimizationCacheScript.Split(' ')[0];

        // The module and the command are looked up separately, so only a Windows without the module skips: a name
        // the module does not have, or a switch the command does not take, fails.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            "if ($null -eq (Get-Module -ListAvailable -Name DeliveryOptimization)) { '__SM_NO_MODULE__' } " +
            $"else {{ $c = Get-Command -Name '{name}' -Module DeliveryOptimization -ErrorAction SilentlyContinue ; " +
            "'found=' + ($null -ne $c) ; if ($c) { 'force=' + $c.Parameters.ContainsKey('Force') } }");

        if (output.Contains("__SM_NO_MODULE__", StringComparison.Ordinal))
            Assert.Skip("this Windows has no DeliveryOptimization module, so Deep Cleanup reports the bucket as not emptied.");
        Assert.Contains("found=True", output, StringComparison.Ordinal);
        Assert.Contains("force=True", output, StringComparison.Ordinal);
    }
}
