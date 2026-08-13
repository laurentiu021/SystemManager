// SysManager · PowerPlans
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// The GUIDs Windows assigns to its three stock power plans.
/// <para>
/// These identify a plan reliably; its display NAME does not. The name is editable by the user and
/// localized by Windows, so deciding "which plan is active" from the name mislabels a renamed or
/// duplicated plan and fails outright on a non-English system. Everything that identifies a plan
/// matches on these values, and falls back to the name only when no GUID matches — which happens
/// exactly when the user duplicated a stock scheme (a copy keeps the name but gets a new GUID).
/// </para>
/// <para>
/// Declared here, in Models, because both the model and the service that reads powercfg need them;
/// a model cannot depend on a service. <c>PerformanceService</c> re-exports them so its existing
/// call sites and tests keep their current names.
/// </para>
/// </summary>
internal static class PowerPlans
{
    /// <summary>Balanced — the Windows default.</summary>
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    /// <summary>High performance.</summary>
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    /// <summary>Ultimate Performance — hidden by default; present on Pro/Workstation editions.</summary>
    public const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";
}
