// SysManager · ArchitectureTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
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
public partial class ArchitectureTests
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

    /// <summary>
    /// Every command a ViewModel exposes must be reachable from the markup — bound in a View, in
    /// App.xaml, or invoked by another ViewModel.
    /// </summary>
    /// <remarks>
    /// <para>The ratchet for a defect class this codebase has hit repeatedly, most starkly with "row
    /// highlight": a feature commit added the models and the ViewModel commands, updated the CHANGELOG,
    /// closed two issues — and touched no view. The announced feature had no button for months. Nothing
    /// caught it, because a command with no binding still compiles, still passes its own unit tests, and
    /// still runs correctly when invoked from a test. The absence only exists in the markup.</para>
    /// <para>Reflection over the generated <c>IRelayCommand</c> properties is what makes this
    /// mechanical rather than a habit: <c>[RelayCommand]</c> generates one property per command, so the
    /// list of things that MUST be reachable is derivable, and a newly added command joins the check
    /// automatically. A command invoked only from C# (chained by another ViewModel) is legitimate and
    /// counts as reachable.</para>
    /// </remarks>
    [Fact]
    public void EveryViewModelCommand_IsReachableFromTheUi()
    {
        var appDir = FindAppProjectDir();
        var markup = Directory
            .GetFiles(appDir, "*.xaml", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();
        Assert.NotEmpty(markup);   // else every assertion below would pass vacuously

        // C# lines, so a command whose work is triggered from code is not reported as dead. DECLARATION
        // lines are excluded up front: `private void OpenChangelog() => …` itself contains
        // "OpenChangelog(", so a naive substring search finds every method's own signature and the whole
        // check passes vacuously — it would assert nothing at all while looking thorough.
        var callLines = Directory
            .GetFiles(Path.Combine(appDir, "ViewModels"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Select(l => l.Trim())
            .Where(l => !DeclarationLine().IsMatch(l))
            .ToList();
        Assert.NotEmpty(callLines);

        var unreachable = new List<string>();

        foreach (var type in AppAssembly.GetTypes()
                     .Where(t => t.Namespace == "SysManager.ViewModels" && !t.IsNested))
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!prop.Name.EndsWith("Command", StringComparison.Ordinal)) continue;
                if (!typeof(System.Windows.Input.ICommand).IsAssignableFrom(prop.PropertyType)) continue;

                if (markup.Any(m => m.Contains(prop.Name, StringComparison.Ordinal))) continue;

                // Invoked through the command object from code.
                if (callLines.Any(l => l.Contains($"{prop.Name}.Execute", StringComparison.Ordinal))) continue;

                // …or the underlying METHOD is called directly, which is the common shape here: a poll
                // loop awaits RefreshTemperaturesAsync() and a dismiss path calls DismissQuickAction(),
                // so the generated command is a redundant wrapper rather than a dead feature. Checking
                // only ".Execute" reported three such commands as unreachable — a false positive that
                // would have pushed pointless buttons into the UI just to satisfy this test.
                var method = prop.Name[..^"Command".Length];
                if (callLines.Any(l => l.Contains($"{method}(", StringComparison.Ordinal)
                                    || l.Contains($"{method}Async(", StringComparison.Ordinal))) continue;

                unreachable.Add($"{type.Name}.{prop.Name}");
            }
        }

        Assert.True(unreachable.Count == 0,
            "These commands exist on a ViewModel but nothing in the UI binds them and no other " +
            "ViewModel invokes them, so a user cannot reach the feature they implement. Either bind " +
            "them in the View or remove them — shipping an unreachable command means the CHANGELOG " +
            "can announce a feature that does not exist:\n  " + string.Join("\n  ", unreachable));
    }

    /// <summary>
    /// The gold elevation banner means one thing and only one thing: you are running as administrator,
    /// so MORE is available. Every tab that shows it must say so.
    /// </summary>
    /// <remarks>
    /// <para>The project's UI contract reserves the golden/amber treatment for elevation-unlocks-more,
    /// with purple for primary actions and neutral for everything else. 30 views render this banner, and
    /// they are hand-written copies of each other, so the convention is held together by nothing but
    /// whoever wrote the last one.</para>
    /// <para>It had already broken once. The Uninstaller tab — the one place where elevation DISABLES a
    /// feature, because each app's own uninstaller wants to raise its own UAC prompt — used the identical
    /// gold treatment to say "Uninstall is disabled in administrator sessions. Reopen SysManager normally
    /// to continue." So the colour that had taught the user "press this, get more" on 29 other tabs was,
    /// on that one, the colour of a dead end, attached to an instruction the app offers no control for
    /// (there is no de-elevation path — <c>AdminHelper.RelaunchAsAdmin</c> goes one way only). That tab
    /// now uses the neutral treatment; this test is why it cannot quietly go back.</para>
    /// </remarks>
    [Fact]
    public void EveryGoldElevationBanner_PromisesMoreAccess()
    {
        var viewsDir = Path.Combine(FindAppProjectDir(), "Views");
        var offenders = new List<string>();
        var banners = 0;

        foreach (var file in Directory.GetFiles(viewsDir, "*.xaml"))
        {
            foreach (var message in GoldElevatedBannerMessages(File.ReadAllText(file)))
            {
                banners++;
                if (!message.StartsWith("Running as administrator", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: \"{message}\"");
            }
        }

        // Guards the guard: if the parse stops finding banners, every assertion below passes vacuously.
        Assert.True(banners >= 25,
            $"Only {banners} gold elevation banners parsed — the check is not reading the markup it thinks it is.");

        Assert.True(offenders.Count == 0,
            "These tabs use the gold elevation banner — which everywhere else in the app means \"you are " +
            "elevated, so you can now do more\" — to say something else. Rewrite the message to start " +
            "\"Running as administrator — …\", or, if elevation genuinely does not unlock more on that tab, " +
            "use the neutral Surface2/Border1 treatment instead, so the colour does not contradict the " +
            "words:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Messages inside a Border that is (a) shown WHEN elevated and (b) painted with the gold
    /// WarningBgSubtle. Parsed rather than grepped: the not-elevated banner sits in the same
    /// <c>Grid.Row</c> in every one of these views, so a file-wide text search would read the wrong one.
    /// </summary>
    private static List<string> GoldElevatedBannerMessages(string xamlText)
    {
        var found = new List<string>();
        System.Xml.Linq.XDocument doc;
        try { doc = System.Xml.Linq.XDocument.Parse(xamlText); }
        catch (System.Xml.XmlException) { return found; }

        foreach (var border in doc.Descendants().Where(e => e.Name.LocalName == "Border"))
        {
            var visibility = (string?)border.Attribute("Visibility") ?? "";
            if (!visibility.Contains("IsElevated", StringComparison.Ordinal)) continue;
            // Inverse == shown when NOT elevated, which is the other banner in the same slot.
            if (visibility.Contains("Inverse", StringComparison.Ordinal)) continue;
            if (!((string?)border.Attribute("Background") ?? "").Contains("WarningBgSubtle", StringComparison.Ordinal)) continue;

            foreach (var block in border.Descendants().Where(e => e.Name.LocalName == "TextBlock"))
            {
                var text = (string?)block.Attribute("Text") ?? "";
                // Skip the icon glyph (one private-use codepoint) and bound values.
                if (text.Length < 20 || text.StartsWith('{')) continue;
                found.Add(WhitespaceRun().Replace(text, " ").Trim());
            }
        }
        return found;
    }

    /// <summary>
    /// A method DECLARATION rather than a call — `private void Foo()`, `private async Task FooAsync(`,
    /// `internal Task Foo(`. Used to drop declaration lines before searching for call sites, so a
    /// method is never counted as its own caller.
    /// </summary>
    [GeneratedRegex(@"^\s*(private|internal|public|protected)\b.*\b\w+\s*\(", RegexOptions.Compiled)]
    private static partial Regex DeclarationLine();

    /// <summary>Collapses the line breaks XAML allows inside an attribute value.</summary>
    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRun();

    /// <summary>The app project directory — .xaml is not copied to the test output.</summary>
    private static string FindAppProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "SysManager", "SysManager.csproj");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "SysManager");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the SysManager app project from " + AppContext.BaseDirectory);
    }
}
