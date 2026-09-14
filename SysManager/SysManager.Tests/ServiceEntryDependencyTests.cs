// SysManager · ServiceEntryDependencyTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Tests for how a <see cref="ServiceEntry"/> words what else depends on it (#1512).
/// </summary>
/// <remarks>
/// The Services tab could not say what breaks when you turn a service off: nothing in the project read
/// <c>DependentServices</c>, so a Stop prompt offered "This may affect system functionality" — true of
/// every service on the machine and therefore not something anyone can decide against.
/// <para>The formatting is tested here rather than through the view model because it is pure: no
/// registry, no service control manager, no elevation. The prompts that consume it are covered in
/// <c>ServicesViewModelTests</c>.</para>
/// </remarks>
public class ServiceEntryDependencyTests
{
    private static ServiceEntry Entry(params string[] dependents) => new()
    {
        Name = "TestSvc",
        DisplayName = "Test Service",
        SafetyDescription = "A curated note.",
        DependentServices = dependents
    };

    [Fact]
    public void NoDependents_SaysNothing()
    {
        var entry = Entry();

        Assert.False(entry.HasDependents);
        Assert.Equal("", entry.DependentNames);
        Assert.Equal("", entry.DependencyWarning);
    }

    [Fact]
    public void OneDependent_NamesItVerbatim()
    {
        var entry = Entry("Windows Fax");

        Assert.True(entry.HasDependents);
        Assert.Equal("Windows Fax", entry.DependentNames);
        Assert.Equal("Other services depend on this one: Windows Fax.", entry.DependencyWarning);
    }

    [Fact]
    public void SeveralDependents_AreCommaSeparatedInTheOrderGiven()
    {
        // Order is the service control manager's, not alphabetical: it is the order Windows would act in.
        var entry = Entry("Charlie", "Alpha", "Bravo");

        Assert.Equal("Charlie, Alpha, Bravo", entry.DependentNames);
    }

    [Fact]
    public void ExactlyFiveDependents_AreAllListedWithNoOverflowPhrase()
    {
        // The boundary on the "all of them" side. Five is the cap, so five must still be a plain list —
        // an off-by-one here would print ", and 0 more".
        var entry = Entry("One", "Two", "Three", "Four", "Five");

        Assert.Equal("One, Two, Three, Four, Five", entry.DependentNames);
        Assert.DoesNotContain("more", entry.DependentNames, StringComparison.Ordinal);
    }

    [Fact]
    public void SixDependents_ListFiveAndCountTheRest()
    {
        // The boundary on the overflow side, and the one that catches a wrong subtraction: six dependents
        // means exactly one is not named.
        var entry = Entry("One", "Two", "Three", "Four", "Five", "Six");

        Assert.Equal("One, Two, Three, Four, Five, and 1 more", entry.DependentNames);
        Assert.DoesNotContain("Six", entry.DependentNames, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyDependents_CountTheUnnamedOnesCorrectly()
    {
        // A service with 25 dependents exists on an ordinary Windows install — measured, not invented —
        // which is the reason the list is capped at all: an unbounded one pushes a confirmation dialog's
        // own buttons off screen, so the prompt asking "are you sure" becomes unanswerable.
        var entry = Entry([.. Enumerable.Range(1, 25).Select(i => "Svc" + i)]);

        Assert.EndsWith(", and 20 more", entry.DependentNames, StringComparison.Ordinal);
        Assert.StartsWith("Svc1, Svc2, Svc3, Svc4, Svc5,", entry.DependentNames, StringComparison.Ordinal);
        Assert.DoesNotContain("Svc6", entry.DependentNames, StringComparison.Ordinal);
    }

    [Fact]
    public void SafetyTooltip_WithNoDependents_IsTheSafetyDescriptionUnchanged()
    {
        // Seven rows in eight have no dependents, so this is the common case: the tooltip must not gain
        // stray whitespace or an empty trailing line for the majority of the grid.
        Assert.Equal("A curated note.", Entry().SafetyTooltip);
    }

    [Fact]
    public void SafetyTooltip_WithDependents_KeepsBothFactsSeparated()
    {
        var entry = Entry("Windows Fax");

        Assert.Equal("A curated note.\n\nOther services depend on this one: Windows Fax.", entry.SafetyTooltip);
    }

    [Fact]
    public void SafetyTooltip_WithNoSafetyDescription_IsTheWarningAlone()
    {
        // A service with no curated note must not get a tooltip that opens with a blank line.
        var entry = new ServiceEntry
        {
            Name = "TestSvc",
            DisplayName = "Test Service",
            SafetyDescription = "",
            DependentServices = ["Windows Fax"]
        };

        Assert.Equal("Other services depend on this one: Windows Fax.", entry.SafetyTooltip);
    }

    [Fact]
    public void ADefaultEntry_HasNoDependents_SoNothingReadsAnUninitialisedList()
    {
        // Every ServiceEntry built anywhere that does not set the property — including the ones in other
        // tests — must report no dependents rather than throwing on a null list.
        var entry = new ServiceEntry { Name = "X", DisplayName = "X" };

        Assert.False(entry.HasDependents);
        Assert.Empty(entry.DependentServices);
        Assert.Equal("", entry.DependencyWarning);
    }
}
