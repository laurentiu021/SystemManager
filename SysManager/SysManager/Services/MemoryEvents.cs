// SysManager · MemoryEvents
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Frozen;

namespace SysManager.Services;

/// <summary>
/// The events that report on the PC's RAM, by the IDs the providers' own manifests give them
/// (<c>wevtutil gp &lt;provider&gt; /ge /gm:true</c>). The memory check counts with these, and System Logs explains the
/// memory test's results with them, so the two cannot disagree again (#2715).
/// </summary>
public static class MemoryEvents
{
    public const string WheaProvider = "Microsoft-Windows-WHEA-Logger";
    public const string DiagnosticProvider = "Microsoft-Windows-MemoryDiagnostics-Results";

    /// <summary>
    /// The WHEA-Logger events the manifest gives to memory: 22 and 46, "A fatal hardware error has occurred. Component:
    /// Memory"; 23 and 47, "A corrected hardware error has occurred. Component: Memory"; 48, "A fatal memory error has
    /// occurred."; and 49, "A corrected memory error has occurred.".
    /// </summary>
    /// <remarks>
    /// The check counted 17 to 20. 17 is a PCI Express error, given by bus, device and function, 18 and 19 are the
    /// processor core's, and 20 the AMD northbridge's, so a laptop logging 17s from a power-saving port was told to test
    /// its RAM.
    /// </remarks>
    public static readonly FrozenSet<int> WheaMemoryErrors = new[] { 22, 23, 46, 47, 48, 49 }.ToFrozenSet();

    /// <summary>
    /// Windows Memory Diagnostic results that found errors: 1102 and 1202, "The Windows Memory Diagnostic tested the
    /// computer's memory and detected hardware errors".
    /// </summary>
    public static readonly FrozenSet<int> DiagnosticFoundErrors = new[] { 1102, 1202 }.ToFrozenSet();

    /// <summary>
    /// Windows Memory Diagnostic results that found none: 1101 and 1201, "The Windows Memory Diagnostic tested the
    /// computer's memory and detected no errors". The memory check and System Logs read 1201 as errors found.
    /// </summary>
    public static readonly FrozenSet<int> DiagnosticFoundNone = new[] { 1101, 1201 }.ToFrozenSet();

    /// <summary>Whether the event is a WHEA error that the manifest gives to memory.</summary>
    public static bool IsWheaMemoryError(string? provider, int id) =>
        string.Equals(provider, WheaProvider, StringComparison.OrdinalIgnoreCase) && WheaMemoryErrors.Contains(id);

    /// <summary>Whether the event is a Windows Memory Diagnostic result that found errors.</summary>
    public static bool IsDiagnosticError(string? provider, int id) =>
        string.Equals(provider, DiagnosticProvider, StringComparison.OrdinalIgnoreCase)
        && DiagnosticFoundErrors.Contains(id);
}
