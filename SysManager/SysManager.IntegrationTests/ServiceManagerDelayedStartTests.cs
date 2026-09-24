// SysManager · ServiceManagerDelayedStartTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Proves the Services scan reads the delayed-start flag Windows actually reports (#2428), by comparing every
/// row with WMI's own view of the same services.
/// </summary>
/// <remarks>
/// The unit suite pins what the flag does once it is on a row — the snapshot, the ledger, the sc.exe token —
/// with the flag handed to it. None of that shows the scan ever sets it: a native query that failed for every
/// service would leave every row "not delayed", which is the bug being fixed, and every unit test would stay
/// green. WMI's <c>Win32_Service</c> is the independent reading: services.msc's "Automatic (Delayed Start)" is
/// <c>StartMode = Auto</c> with <c>DelayedAutoStart = true</c>.
/// <para>The comparison is what caught the first design. Reading each service's <c>DelayedAutostart</c>
/// registry value disagreed with WMI for a per-user service instance, which has no value of its own and takes
/// its template's — so the scan asks the service control manager instead.</para>
/// <para>Read-only, and needs no elevation. Assertions report counts only: a failure message ends up in a
/// public CI log, and the service names on the machine running it are not something to publish.</para>
/// </remarks>
public class ServiceManagerDelayedStartTests
{
    [Fact]
    public void GetAllServices_ReadsTheDelayedStartThatWindowsReports()
    {
        var windows = ReadWindowsView();
        var scan = ServiceManagerService.GetAllServices();

        var compared = 0;
        var delayed = 0;
        var disagreements = 0;
        foreach (var entry in scan)
        {
            // A service installed or removed between the two readings is in only one of them.
            if (!windows.TryGetValue(entry.Name, out var delayedPerWindows)) continue;

            compared++;
            if (delayedPerWindows) delayed++;
            if (entry.IsDelayedAutoStart != delayedPerWindows) disagreements++;
        }

        // Floors, so an empty reading cannot pass as agreement: any Windows install has hundreds of services.
        Assert.True(compared >= 20,
            $"only {compared} services appeared in both the scan and WMI, so one of the two readings is broken.");
        if (delayed == 0)
            Assert.Skip($"none of the {compared} services on this host is Automatic (Delayed Start), so there is "
                        + "no delayed flag to compare.");

        Assert.True(disagreements == 0,
            $"{disagreements} of {compared} services disagree with WMI about a delayed start "
            + $"({delayed} are delayed according to WMI).");
    }

    /// <summary>Service name → whether WMI reports it as Automatic (Delayed Start).</summary>
    private static Dictionary<string, bool> ReadWindowsView()
    {
        var view = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher("SELECT Name, StartMode, DelayedAutoStart FROM Win32_Service");
        using var results = searcher.Get();
        foreach (ManagementObject service in results)
        {
            using (service)
            {
                view[(string)service["Name"]] =
                    (string)service["StartMode"] == "Auto" && service["DelayedAutoStart"] is true;
            }
        }
        return view;
    }
}
