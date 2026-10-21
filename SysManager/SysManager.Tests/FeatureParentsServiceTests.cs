// SysManager · FeatureParentsServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="FeatureParentsService.Walk"/>: which features turning one on turns on with it (#2834). The
/// walk is handed DISM's answers, so nothing here asks the DISM API; the integration suite does.
/// </summary>
public class FeatureParentsServiceTests
{
    private const int Staged = 2;

    private static Func<string, IReadOnlyList<(string Name, int State)>?> Graph(
        Dictionary<string, (string Name, int State)[]> parents) =>
        name => parents.TryGetValue(name, out var found) ? found : [];

    [Fact]
    public void Walk_ListsEveryFeatureItNeeds_NearestFirst_AndEachOnce()
    {
        // A needs B and C, and both need D: -All turns on all three, and D once.
        var walked = FeatureParentsService.Walk("A", Graph(new()
        {
            ["A"] = [("B", Staged), ("C", Staged)],
            ["B"] = [("D", Staged)],
            ["C"] = [("D", Staged)],
        }));

        Assert.Equal(["B", "C", "D"], walked!.Select(p => p.Name));
    }

    [Fact]
    public void Walk_GoesOnThroughAParentThatIsOn()
    {
        // Turning A off leaves B and its own parent C on, so both are listed.
        var walked = FeatureParentsService.Walk("A", Graph(new()
        {
            ["A"] = [("B", FeatureParentsService.Installed)],
            ["B"] = [("C", FeatureParentsService.Installed)],
        }));

        Assert.Equal([new FeatureParent("B", true, false), new FeatureParent("C", true, false)], walked);
    }

    [Theory]
    [InlineData(0, false, false)]   // not present
    [InlineData(1, false, true)]    // turns off at the next restart
    [InlineData(2, false, false)]   // staged: off
    [InlineData(3, false, false)]   // removed
    [InlineData(4, true, false)]    // installed: on
    [InlineData(5, true, true)]     // turns on at the next restart
    [InlineData(6, false, false)]   // superseded
    public void Walk_ReadsDismsStates(int state, bool on, bool pending)
    {
        var parent = Assert.Single(FeatureParentsService.Walk("A", Graph(new() { ["A"] = [("B", state)] }))!);

        Assert.Equal(on, parent.IsEnabled);
        Assert.Equal(pending, parent.Pending);
    }

    [Fact]
    public void Walk_WhenDismDoesNotAnswer_KnowsNothing()
        => Assert.Null(FeatureParentsService.Walk("A", name => name == "A" ? [("B", Staged)] : null));

    [Fact]
    public void Walk_EndsOnALoop_AndNeverListsTheFeatureItself()
    {
        var walked = FeatureParentsService.Walk("A", Graph(new()
        {
            ["A"] = [("B", Staged)],
            ["B"] = [("A", Staged), ("C", Staged)],
            ["C"] = [("B", Staged)],
        }));

        Assert.Equal(["B", "C"], walked!.Select(p => p.Name));
    }

    [Fact]
    public void Walk_StopsAtItsLimit_OnAnEndlessChain()
    {
        var walked = FeatureParentsService.Walk(
            "F0", name => [($"F{int.Parse(name[1..], CultureInfo.InvariantCulture) + 1}", Staged)]);

        Assert.Equal(FeatureParentsService.MaxParents, walked!.Count);
    }

    [Fact]
    public void EveryDismCall_LoadsDismapiFromSystem32Only()
    {
        // dismapi.dll is not a DLL Windows always loads from System32, and SysManager runs elevated: without the
        // attribute, a dismapi.dll beside the exe would be loaded first.
        var calls = typeof(FeatureParentsService)
            .GetNestedType("NativeMethods", BindingFlags.NonPublic)!
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(m => m.Name.StartsWith("Dism", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(6, calls.Count);
        Assert.All(calls, m => Assert.Equal(DllImportSearchPath.System32,
            m.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths));
    }

    [Fact]
    public void Walk_SkipsANameDismLeftBlank()
        => Assert.Equal(["B"], FeatureParentsService.Walk("A", Graph(new() { ["A"] = [("", Staged), ("B", Staged)] }))!
            .Select(p => p.Name));
}
