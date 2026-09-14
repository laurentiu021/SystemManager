// SysManager · ServiceManagerDependencyTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Proves <see cref="ServiceManagerService.GetAllServices"/> actually reads the dependency information
/// the Services tab now shows (#1512), against the real service control manager.
/// </summary>
/// <remarks>
/// This lives here and not in the unit suite because the unit tests construct <see cref="ServiceEntry"/>
/// directly with a dependency list handed to them. They pin how the fact is WORDED, and they stayed green
/// when the read was deleted from the service entirely — so on their own they proved the formatting of
/// data that nothing supplied.
/// <para>Reading service metadata needs no elevation and mutates nothing. Assertions report counts only:
/// a failure message ends up in a public CI log, and the service names on the machine running it are not
/// something to publish.</para>
/// </remarks>
public class ServiceManagerDependencyTests
{
    [Fact]
    public void GetAllServices_ReadsDependentsFromTheServiceControlManager()
    {
        var all = ServiceManagerService.GetAllServices();

        // A floor rather than an exact number: any Windows install has hundreds, and this only has to
        // catch an enumeration that returned nothing, which would make every assertion below vacuous.
        Assert.True(all.Count >= 20, $"Expected the scan to return at least 20 services, got {all.Count}.");

        var withDependents = all.Where(s => s.HasDependents).ToList();
        var edges = all.Sum(s => s.DependentServices.Count);

        // The assertion that fails when the read is removed. Windows ships services that carry others —
        // Remote Procedure Call alone carries dozens — so zero here means the property is never populated,
        // not that the machine is unusual.
        Assert.True(withDependents.Count > 0,
            $"No service reported a dependent across {all.Count} services, so the dependency read is not "
            + "reaching the service control manager. Every prompt that names what else stops is dead.");
        Assert.True(edges >= withDependents.Count,
            $"{withDependents.Count} services report having dependents but only {edges} dependency edges "
            + "were counted, which is arithmetically impossible.");
    }

    [Fact]
    public void EveryDependentName_IsReadable()
    {
        // A ServiceController's DisplayName can throw or come back blank if the service went away between
        // the enumeration and the read. Those are dropped rather than added as empty strings, because an
        // empty name in the prompt would read as a missing word.
        var all = ServiceManagerService.GetAllServices();

        var blank = all.SelectMany(s => s.DependentServices).Count(string.IsNullOrWhiteSpace);

        Assert.Equal(0, blank);
    }

    [Fact]
    public void HasDependents_AgreesWithTheListOnEveryRow()
    {
        // The model's flag and the list it is derived from cannot be allowed to disagree: the prompts
        // branch on the flag and then interpolate the list, so a row where they differ produces either a
        // sentence with no names in it or names nobody is shown.
        var all = ServiceManagerService.GetAllServices();

        var mismatched = all.Count(s => s.HasDependents != (s.DependentServices.Count > 0));
        var emptyWarnings = all.Count(s => s.HasDependents && s.DependencyWarning.Length == 0);

        Assert.Equal(0, mismatched);
        Assert.Equal(0, emptyWarnings);
    }

    [Fact]
    public void NoService_ListsItselfAmongItsDependents()
    {
        // A sanity check on the read direction. If this ever fired, the prompt would tell the user that
        // stopping a service also stops itself.
        var all = ServiceManagerService.GetAllServices();

        var selfReferencing = all.Count(s =>
            s.DependentServices.Contains(s.DisplayName, StringComparer.OrdinalIgnoreCase));

        Assert.Equal(0, selfReferencing);
    }

    [Fact]
    public void EveryRow_HasANonEmptySafetyTooltip()
    {
        // The pill's tooltip is now a computed property rather than a stored string. Every row still needs
        // something in it: an empty tooltip renders as a stray empty popup on hover.
        var all = ServiceManagerService.GetAllServices();

        var empty = all.Count(s => string.IsNullOrWhiteSpace(s.SafetyTooltip));

        Assert.Equal(0, empty);
    }
}
