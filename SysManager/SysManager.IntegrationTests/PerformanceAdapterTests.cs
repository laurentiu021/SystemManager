// SysManager · PerformanceAdapterTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Which display adapters are in the PC, read through WMI and the registry of the PC running the tests, which are only
/// read.
/// </summary>
public class PerformanceAdapterTests
{
    [Fact]
    public void TheAdaptersInThePC_AreDisplayClassSubkeysThatExist()
    {
        var present = PerformanceService.PresentAdapterKeys();
        if (present is null) Assert.Skip("This PC's display adapters could not be read through WMI.");

        using var classRoot = Registry.LocalMachine.OpenSubKey(PerformanceService.GpuClassRoot);
        Assert.NotNull(classRoot);
        var subKeys = classRoot.GetSubKeyNames();
        Assert.NotEmpty(present);
        Assert.All(present, key => Assert.Contains(key, subKeys));
    }
}
