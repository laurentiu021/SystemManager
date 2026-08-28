// SysManager · PowerShellRunnerTelemetryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The hosted PowerShell must not be able to report anything anywhere.
/// </summary>
/// <remarks>
/// The in-process runspace is the PowerShell 7 SDK, and <c>System.Management.Automation</c> ships
/// <c>Microsoft.ApplicationInsights</c> for its telemetry subsystem — resolved in the dependency graph and
/// bundled into the self-contained single-file .exe. PowerShell gates that subsystem on one environment
/// variable and nothing else, so the opt-out is the whole defence and deserves a test rather than trust.
/// </remarks>
public class PowerShellRunnerTelemetryTests
{
    [Fact]
    public void TouchingTheRunner_OptsPowerShellOutOfTelemetry()
    {
        // Calls the opt-out the way CreateRunspace does. Deliberately NOT by reading
        // TelemetryOptOutVariable: it is a const, so C# inlines the literal and initialises nothing — the
        // first version of this test did exactly that and was red for a real reason.
        PowerShellRunner.OptOutOfPowerShellTelemetry();

        // Read back through the LITERAL name, not through PowerShellRunner.TelemetryOptOutVariable.
        // Using the constant on both sides makes the test self-consistent: rename it and the setter and the
        // assertion move together, agreeing on a variable PowerShell does not read. Found by mutation —
        // this test stayed green when the constant was typoed.
        Assert.Equal(
            "1",
            Environment.GetEnvironmentVariable(
                "POWERSHELL_TELEMETRY_OPTOUT", EnvironmentVariableTarget.Process));
    }

    [Fact]
    public void TheOptOut_NamesTheVariablePowerShellActuallyReads()
    {
        // Pinned as a literal: a typo here would leave telemetry on while every other test stayed green,
        // because nothing else in the app consults this name.
        Assert.Equal("POWERSHELL_TELEMETRY_OPTOUT", PowerShellRunner.TelemetryOptOutVariable);
    }

    [Fact]
    public void TheOptOut_DoesNotWriteTheUsersStoredEnvironment()
    {
        // Process scope only. Writing the user or machine scope would be a side effect nobody asked for,
        // and those scopes are what EnvironmentVariableService edits on the user's behalf.
        Assert.Null(Environment.GetEnvironmentVariable(
            "POWERSHELL_TELEMETRY_OPTOUT", EnvironmentVariableTarget.User));
    }
}
