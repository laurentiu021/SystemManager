// SysManager · ArchitectureTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
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

    /// <summary>
    /// Any test class that swaps <c>DialogService.Instance</c> must be in the serialized collection.
    /// <para>
    /// The collection exists and 24 classes use it correctly, but the attribute is easy to forget and
    /// nothing enforced it: <c>DebloaterViewModelTests</c> and <c>DialogServiceTests</c> both touched
    /// the static while running in PARALLEL with the serialized group, because
    /// <c>parallelizeTestCollections</c> is true. The failure mode is not a clean crash — one class
    /// restores the singleton to a value another class is still using, so a confirmation gate answers
    /// with a foreign canned answer and a destructive-op test passes for the wrong reason.
    /// </para>
    /// <para>
    /// Source-level, deliberately. The attribute is a compile-time fact about a test class, so a
    /// reflection scan over the test assembly would work too — but reading the source also catches a
    /// class that swaps the static inside a helper, and the same source-scan approach is already used
    /// for the destructive-op logging guard in ActivityLogServiceTests.
    /// </para>
    /// </summary>
    [Fact]
    public void DialogServiceSwappers_AreInTheSerializedCollection()
    {
        var testDir = FindTestSourceDirectory();
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(testDir, "*.cs"))
        {
            var name = Path.GetFileName(file);
            if (name is "DialogAnswer.cs" or "ArchitectureTests.cs") continue;   // the helper and this test

            var source = File.ReadAllText(file);

            // Either shape counts: assigning the static directly, or using the scoped helper — both
            // install a substitute into process-wide state for the duration of the test.
            var swaps = source.Contains("DialogService.Instance =", StringComparison.Ordinal)
                     || source.Contains("new DialogAnswer(", StringComparison.Ordinal);
            if (!swaps) continue;

            if (!source.Contains("[Collection(\"DialogService\")]", StringComparison.Ordinal))
                offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "These test classes swap the process-wide DialogService.Instance but are not in the " +
            "serialized \"DialogService\" collection, so they race the classes that are — a test can " +
            "restore a substitute another test is still using. Add [Collection(\"DialogService\")]:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding this project's sources. The build copies
    /// no .cs files to the output, so the assembly location alone cannot answer this.
    /// </summary>
    private static string FindTestSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SysManager.Tests.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the SysManager.Tests source directory from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Every static method that RESOLVES a user-data path must let the caller redirect it.
    /// <para>
    /// The sibling ratchet above scans <c>static readonly</c> string FIELDS. This closes the adjacent
    /// shape it cannot see: a static METHOD that resolves the profile with no override parameter at
    /// all. <c>UpdateApplier.PreviousBuildPath</c> is why the gap was noticed — the field scan walked
    /// straight past it — though that method did already take an optional <c>updatesDir</c>. The
    /// actual #1772 defect was one level up, in its CALLER, and is pinned behaviourally by
    /// <c>UpdateApplierTests.ApplyCopy_DoesNotTouchTheRealProfile</c>; that assertion fails against
    /// the unfixed code, which is the evidence this reflection scan cannot provide.
    /// </para>
    /// <para>
    /// The rule this encodes is the narrow, mechanically checkable half: if a static member can
    /// produce a path under the user profile, it must at least offer an override. A default is fine —
    /// having no parameter to override is not.
    /// </para>
    /// </summary>
    [Fact]
    public void StaticPathMethods_AcceptARedirect()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(profile));   // else every check below would be vacuous

        var offenders = new List<string>();

        foreach (var type in AppAssembly.GetTypes()
                     .Where(t => t.Namespace == "SysManager.Services" && !t.IsNested))
        {
            foreach (var method in type.GetMethods(
                         BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                if (method.ReturnType != typeof(string)) continue;
                if (method.IsSpecialName) continue;   // property getters are covered by the field scan

                // Only parameterless-invocable methods can be probed. One that REQUIRES arguments is
                // not a silent default in the first place — the caller had to decide something.
                var parameters = method.GetParameters();
                if (parameters.Any(p => !p.IsOptional)) continue;

                string? value;
                try
                {
                    value = method.Invoke(null, parameters.Select(p => p.DefaultValue).ToArray()) as string;
                }
                catch (TargetInvocationException)
                {
                    continue;   // a method that throws on defaults cannot silently write anywhere
                }

                if (string.IsNullOrEmpty(value)) continue;
                if (!value.StartsWith(profile, StringComparison.OrdinalIgnoreCase)) continue;

                // It resolves under the profile — that is allowed, but ONLY if a caller can override it.
                if (parameters.Length == 0)
                    offenders.Add($"{type.Name}.{method.Name}() takes no override parameter");
            }
        }

        Assert.True(offenders.Count == 0,
            "These static members resolve a path under the user's profile with no way for a caller to " +
            "redirect it, so any test reaching them operates on REAL user data. Add an optional " +
            "override parameter — and thread it through every caller, because an override nobody can " +
            "pass is not a seam:\n  " + string.Join("\n  ", offenders));
    }
}
