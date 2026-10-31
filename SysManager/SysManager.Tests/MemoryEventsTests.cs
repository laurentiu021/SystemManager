// SysManager · MemoryEventsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The memory events by the IDs the providers' manifests give them, each with the manifest's wording beside it
/// (<c>wevtutil gp &lt;provider&gt; /ge /gm:true</c>).
/// </summary>
/// <remarks>
/// The memory check counted WHEA 17 to 20, which are PCI Express and processor errors, and Windows Memory Diagnostic's
/// 1201, a test that found no errors, so a laptop logging PCI Express errors was told to test its RAM and a passed test
/// read as a warning (#2715). The provider names are written out, as the System log gives them.
/// </remarks>
public class MemoryEventsTests
{
    private const string Whea = "Microsoft-Windows-WHEA-Logger";
    private const string Diagnostic = "Microsoft-Windows-MemoryDiagnostics-Results";

    [Theory]
    [InlineData(17, false)] // "A corrected hardware error has occurred. Component: %5", by bus, device and function
    [InlineData(18, false)] // "A fatal hardware error has occurred. Reported by component: Processor Core"
    [InlineData(19, false)] // "A corrected hardware error has occurred. Reported by component: Processor Core"
    [InlineData(20, false)] // "A fatal hardware error has occurred. Component: AMD Northbridge"
    [InlineData(22, true)]  // "A fatal hardware error has occurred. Component: Memory"
    [InlineData(23, true)]  // "A corrected hardware error has occurred. Component: Memory"
    [InlineData(46, true)]  // "A fatal hardware error has occurred. Component: Memory"
    [InlineData(47, true)]  // "A corrected hardware error has occurred. Component: Memory"
    [InlineData(48, true)]  // "A fatal memory error has occurred."
    [InlineData(49, true)]  // "A corrected memory error has occurred."
    public void AWheaEvent_IsAMemoryError_OnlyWhenTheManifestGivesItToMemory(int id, bool memory) =>
        Assert.Equal(memory, MemoryEvents.IsWheaMemoryError(Whea, id));

    [Theory]
    [InlineData(1101, false)] // "The Windows Memory Diagnostic tested the computer's memory and detected no errors"
    [InlineData(1201, false)] // "The Windows Memory Diagnostic tested the computer's memory and detected no errors"
    [InlineData(1102, true)]  // "... tested the computer's memory and detected hardware errors"
    [InlineData(1202, true)]  // "... tested the computer's memory and detected hardware errors"
    [InlineData(1103, false)] // "The Windows Memory Diagnostic was canceled during execution"
    [InlineData(1104, false)] // "The Windows Memory Diagnostic could not complete its tests of the computer's memory"
    public void AMemoryTestResult_IsAnError_OnlyWhenTheTestFoundErrors(int id, bool error) =>
        Assert.Equal(error, MemoryEvents.IsDiagnosticError(Diagnostic, id));

    [Fact]
    public void AnId_CountsOnlyForItsOwnProvider()
    {
        Assert.False(MemoryEvents.IsWheaMemoryError(Diagnostic, 47));
        Assert.False(MemoryEvents.IsDiagnosticError(Whea, 1202));
        Assert.False(MemoryEvents.IsWheaMemoryError(null, 47));
    }

    [Fact]
    public void TheProviderNames_AreTheOnesTheSystemLogGives()
    {
        Assert.Equal(Whea, MemoryEvents.WheaProvider);
        Assert.Equal(Diagnostic, MemoryEvents.DiagnosticProvider);
    }
}
