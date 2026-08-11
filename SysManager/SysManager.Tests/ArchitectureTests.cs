// SysManager · ArchitectureTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using NetArchTest.Rules;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Architecture fitness functions (NetArchTest) that pin the MVVM dependency direction
/// — View → ViewModel → Service/Model — so a change can't silently reintroduce an upward
/// reference (the class of regression that let the Dashboard shell winget directly instead
/// of going through the injected service). They run in CI against the shipped assembly, so
/// the layering is enforced mechanically rather than by review discipline alone.
/// </summary>
public class ArchitectureTests
{
    // Any public type from the app assembly anchors NetArchTest to SysManager.dll.
    private static Assembly AppAssembly => typeof(WingetService).Assembly;

    private static void AssertNoDependency(string fromNamespace, string onNamespace, string? exceptType = null)
    {
        var predicate = Types.InAssembly(AppAssembly).That().ResideInNamespace(fromNamespace);
        if (exceptType is not null)
            predicate = predicate.And().DoNotHaveName(exceptType);

        var result = predicate.ShouldNot().HaveDependencyOn(onNamespace).GetResult();

        var offenders = result.FailingTypes is null
            ? string.Empty
            : string.Join(", ", result.FailingTypes.Select(t => t.FullName));
        Assert.True(result.IsSuccessful,
            $"{fromNamespace} must not depend on {onNamespace}. Offending types: {offenders}");
    }

    [Fact]
    public void Services_DoNotDependOn_ViewModels()
        => AssertNoDependency("SysManager.Services", "SysManager.ViewModels");

    [Fact]
    public void Services_DoNotDependOn_Views()
        => AssertNoDependency("SysManager.Services", "SysManager.Views");

    // MainWindowViewModel is the shell / navigation view model: its nav table maps each tab
    // to its View type (typeof(Views.XView)) to drive content presentation, so it legitimately
    // references Views. Every OTHER view model must not — a tab VM reaching into Views is the
    // regression this guards. (Moving the nav map to XAML DataTemplates would drop even this
    // one dependency; tracked for the navigation refactor.)
    [Fact]
    public void ViewModels_DoNotDependOn_Views()
        => AssertNoDependency("SysManager.ViewModels", "SysManager.Views", exceptType: "MainWindowViewModel");

    [Theory]
    [InlineData("SysManager.Services")]
    [InlineData("SysManager.ViewModels")]
    [InlineData("SysManager.Views")]
    public void Models_DoNotDependOnUpperLayers(string upperLayer)
        => AssertNoDependency("SysManager.Models", upperLayer);

    /// <summary>
    /// No service may hold a resolved user-data path in STATIC state.
    /// <para>
    /// A <c>static readonly</c> path built from <see cref="Environment.SpecialFolder"/> is untestable
    /// by any means: that API resolves through the Win32 known-folder function and ignores the
    /// <c>LOCALAPPDATA</c> environment variable, so no test — not even one in a child process — can
    /// redirect it. The consequence is not theoretical. <c>SpeedTestHistoryService</c> held its path
    /// that way, so its tests ran against the real profile: one wrote fabricated results into the
    /// user's live speed-test history and two deleted it outright.
    /// </para>
    /// <para>
    /// The fix is the <c>string? configDir = null</c> constructor seam the persistence services
    /// already share. This test is a RATCHET, not a clean-slate assertion: seven services still hold
    /// their path statically and converting all of them is a refactor in its own right, so those are
    /// listed as known. Anything NOT on that list fails immediately, and the list itself must shrink
    /// — a name that no longer offends also fails the test, so it cannot rot into a permanent excuse.
    /// </para>
    /// </summary>
    [Fact]
    public void Services_DoNotHoldUserDataPathsInStaticFields()
    {
        // Known offenders, kept ONLY so this ratchet could be added without a repo-wide refactor in one
        // change. Removing a name from this list is the goal — tracked in issue #1741. FOUR have come
        // off so far:
        //   · ActivityLogService — when the destructive operations started logging, a test asserting
        //     they do would otherwise have written into the user's own activity history.
        //   · AppIconService — not latent at all: five tests called SetNetworkFetchEnabled, which
        //     persists, so the suite overwrote the user's real icon-fetch preference every run (#1758).
        //   · SettingsWatchdogService and WindowsThemeService — both constructed concretely by tests,
        //     both now behind the shared `string? configDir = null` seam.
        //
        // The two that remain are the two hard ones, and they are hard for different reasons:
        //   · ThemeService is heavily static (20 static members) and is reached from XAML resource
        //     resolution, so a constructor seam is a wider refactor than a path change.
        //   · LogService is a `static partial class` by design — Serilog's sink is configured once per
        //     process — so it has no instance to hang a seam on. It is also the only one no test
        //     constructs, so its risk is the lowest of the set.
        // Both need a design decision rather than a mechanical edit; neither is touched here.
        string[] known =
        [
            "LogService.<LogDir>k__BackingField",
            "ThemeService.SettingsPath",
        ];

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(profile));   // else every check below would be vacuous

        var found = new List<string>();

        foreach (var type in AppAssembly.GetTypes()
                     .Where(t => t.Namespace == "SysManager.Services" && !t.IsNested))
        {
            foreach (var field in type.GetFields(
                         BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType != typeof(string)) continue;
                if (!field.IsInitOnly && !field.IsLiteral) continue;

                // A static string is only a problem when it holds a resolved user-data PATH. A bare
                // file name ("speedtest-history.json") or a registry key is fine — what must not be
                // baked into static state is an absolute path under the user's profile, because that
                // is precisely what a test needs to redirect.
                var value = field.IsLiteral
                    ? field.GetRawConstantValue() as string
                    : field.GetValue(null) as string;
                if (string.IsNullOrEmpty(value)) continue;

                if (value.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
                    found.Add($"{type.Name}.{field.Name}");
            }
        }

        var added = found.Except(known).ToList();
        Assert.True(added.Count == 0,
            "These services bake a user-profile path into static state, which makes them impossible to " +
            "point at a temp directory — so any test touching them would operate on REAL user data " +
            "(exactly how SpeedTestHistoryService's tests came to delete the user's speed-test " +
            "history). Add the `string? configDir = null` constructor seam instead:\n  " +
            string.Join("\n  ", added));

        var fixedSince = known.Except(found).ToList();
        Assert.True(fixedSince.Count == 0,
            "These no longer hold a static user-profile path, so remove them from the `known` list " +
            "above — a stale allowance silently weakens this guard:\n  " + string.Join("\n  ", fixedSince));
    }
}
