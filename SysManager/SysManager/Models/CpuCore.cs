// SysManager · CpuCore
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A single logical CPU and its hybrid-core classification. On Intel 12th-gen+
/// hybrid CPUs cores are labelled "Performance" (P-core) or "Efficiency" (E-core)
/// from their Windows EfficiencyClass; on homogeneous CPUs every core is "Standard".
/// </summary>
public sealed record CpuCore(int LogicalIndex, byte EfficiencyClass, string CoreType)
{
    public string Display => $"CPU {LogicalIndex}";
    public bool IsPerformance => CoreType == "Performance";
    public bool IsEfficiency => CoreType == "Efficiency";
}
