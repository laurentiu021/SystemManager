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
    /// Any test class that touches a process-wide mutable singleton must be in the serialized collection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The collection exists and most classes use it correctly, but the attribute is easy to forget and
    /// nothing enforced it: <c>DebloaterViewModelTests</c> and <c>DialogServiceTests</c> both touched
    /// <c>DialogService.Instance</c> while running in PARALLEL with the serialized group, because
    /// <c>parallelizeTestCollections</c> is true. The failure mode is not a clean crash — one class
    /// restores the singleton to a value another class is still using, so a confirmation gate answers
    /// with a foreign canned answer and a destructive-op test passes for the wrong reason.
    /// </para>
    /// <para>
    /// This originally scanned for <c>DialogService.Instance</c> ONLY, and that gap let exactly the same
    /// defect through for a second singleton: <c>CleanupViewModelTests</c> acquired the process-wide
    /// <c>OperationLockService.Instance</c> and asserted which operation held it, with no collection
    /// attribute at all. The list of watched statics is therefore data, not a hardcoded string — adding
    /// the next one is one row.
    /// </para>
    /// <para>
    /// Source-level, deliberately. The attribute is a compile-time fact so reflection would work too, but
    /// reading the source also catches a class that touches the static inside a helper, and the same
    /// approach is already used for the destructive-op logging guard in ActivityLogServiceTests.
    /// </para>
    /// </remarks>
    [Fact]
    public void ProcessWideStaticUsers_AreInTheSerializedCollection()
    {
        // marker → what it is, for the failure message. Both shapes of the dialog swap count: assigning
        // the static directly, or using the scoped DialogAnswer helper.
        (string Marker, string What)[] watched =
        [
            ("DialogService.Instance =", "DialogService.Instance"),
            ("new DialogAnswer(", "DialogService.Instance (via the DialogAnswer helper)"),
            ("OperationLockService.Instance", "OperationLockService.Instance"),
        ];

        var testDir = FindTestSourceDirectory();
        var offenders = new List<string>();
        var inspected = 0;

        foreach (var file in Directory.GetFiles(testDir, "*.cs"))
        {
            var name = Path.GetFileName(file);
            // The helper itself, this test, and the collection definitions are not test classes.
            if (name is "DialogAnswer.cs" or "ArchitectureTests.cs" or "TestCollections.cs") continue;

            var source = File.ReadAllText(file);

            var touched = watched.Where(w => source.Contains(w.Marker, StringComparison.Ordinal))
                                 .Select(w => w.What)
                                 .Distinct()
                                 .ToList();
            if (touched.Count == 0) continue;

            inspected++;
            if (!source.Contains("[Collection(\"ProcessWideStatics\")]", StringComparison.Ordinal))
                offenders.Add($"{name} — touches {string.Join(", ", touched)}");
        }

        // Vacuity floor: if the markers stopped matching, this would pass while inspecting nothing.
        Assert.True(inspected >= 25,
            $"Expected at least 25 classes touching a process-wide singleton, found {inspected} — " +
            "the markers are probably out of date.");

        Assert.True(offenders.Count == 0,
            "These test classes touch a process-wide mutable singleton but are not in the serialized "
            + "\"ProcessWideStatics\" collection, so they race the classes that are — a test can observe "
            + "state another test owns, and pass or fail for a foreign reason. Add "
            + "[Collection(\"ProcessWideStatics\")]:\n  " + string.Join("\n  ", offenders));
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
    /// <c>TextWrapping="Wrap"</c> must not sit inside a horizontal <c>StackPanel</c>, where it does
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>A StackPanel measures its children with infinite width along its orientation, so a TextBlock
    /// inside a horizontal one never learns a width to wrap against: the attribute is inert and the text
    /// is laid out on a single line, with whatever exceeds the panel silently clipped. It reads as
    /// protection and provides none.</para>
    /// <para>14 TextBlocks across 13 views did this, every one of them a warning or an explanation — an
    /// SSD-shredding caveat, a Tamper-Protection notice, admin-requirement banners — so the truncation
    /// dropped the caveat and kept the setup. The correct parent is a <c>DockPanel</c> (glyph docked
    /// left, message filling) or a <c>Grid</c>, both of which give the text a real width. This test keeps
    /// the next hand-written banner from reintroducing the class.</para>
    /// </remarks>
    [Fact]
    public void NoTextWrapping_IsInertInsideAHorizontalStackPanel()
    {
        var viewsDir = Path.Combine(FindAppProjectDir(), "Views");
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.GetFiles(viewsDir, "*.xaml"))
        {
            System.Xml.Linq.XDocument doc;
            try { doc = System.Xml.Linq.XDocument.Load(file); }
            catch (System.Xml.XmlException) { continue; }

            foreach (var tb in doc.Descendants().Where(e => e.Name.LocalName == "TextBlock"))
            {
                scanned++;
                if (((string?)tb.Attribute("TextWrapping") ?? "") != "Wrap") continue;
                if (!InsideHorizontalStackPanel(tb)) continue;

                var text = WhitespaceRun().Replace((string?)tb.Attribute("Text") ?? "", " ").Trim();
                // Bound values and short glyphs cannot meaningfully clip; only real prose does.
                if (text.Length < 40 || text.StartsWith('{')) continue;

                offenders.Add($"{Path.GetFileName(file)}: \"{(text.Length > 60 ? text[..60] + "…" : text)}\"");
            }
        }

        Assert.True(scanned > 100, $"Only {scanned} TextBlocks parsed — the check is not reading the views.");

        Assert.True(offenders.Count == 0,
            "These TextBlocks set TextWrapping=\"Wrap\" but their nearest layout ancestor is a horizontal " +
            "StackPanel, which hands children infinite width — so wrapping does nothing and the text is " +
            "clipped instead. Put the message in a DockPanel (glyph docked left) or a Grid so it has a " +
            "real width to wrap in:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// True if the nearest ancestor that constrains width along an axis is a horizontal StackPanel.
    /// A Grid, DockPanel, Border, ScrollViewer or vertical StackPanel between the TextBlock and any
    /// horizontal StackPanel gives the text a real width, so the decision is made on the FIRST layout
    /// container encountered walking outward — only an unbroken path into a horizontal StackPanel is inert.
    /// </summary>
    private static bool InsideHorizontalStackPanel(System.Xml.Linq.XElement textBlock)
    {
        for (var e = textBlock.Parent; e is not null; e = e.Parent)
        {
            switch (e.Name.LocalName)
            {
                case "StackPanel":
                    // No Orientation attribute defaults to Vertical, which constrains width — not a problem.
                    return (((string?)e.Attribute("Orientation")) ?? "Vertical") == "Horizontal";
                case "Grid":
                case "DockPanel":
                case "Border":
                case "ScrollViewer":
                case "WrapPanel":
                case "UserControl":
                case "GroupBox":
                case "ToolTip":
                    return false;
            }
        }
        return false;
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

    /// <summary>
    /// Every view model that declares an <c>IsActive</c> flag must be handled by
    /// <c>MainWindowViewModel.SetActive</c>, or its poll is never gated by visibility.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SetActive</c> is a hand-maintained switch, so a new polling tab is opted OUT of the gate by
    /// default and nothing about the omission looks wrong — no compiler error, no failing test, and the
    /// tab still works. That is exactly how the Standby List Cleaner ended up running a 2-second
    /// dispatcher tick for the whole session after being opened once: behind another tab, minimised, and
    /// closed to the tray, with an unsupervised privileged purge reachable from it.
    /// </para>
    /// <para>
    /// Reflection rather than a source scan: declaring <c>IsActive</c> is a compile-time fact about the
    /// type, so the assembly is the more reliable source. The switch arms are read from source, because
    /// a <c>switch</c> pattern arm is not visible through reflection.
    /// </para>
    /// <para>
    /// Deliberately keyed on <c>IsActive</c> and not on "owns a timer": <c>IsActive</c> IS the gate
    /// contract. A view model that polls without declaring it would slip past this — which is why the
    /// floor below asserts the check found the flags it expects, so a rename cannot make it vacuous.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryViewModelWithAnIsActiveFlag_IsHandledBySetActive()
    {
        var source = File.ReadAllText(Path.Combine(
            FindAppProjectDir(), "ViewModels", "MainWindowViewModel.cs"));

        // Only the SetActive body counts. Searching the whole file would let an unrelated mention of a
        // view model's name pass the check — the same cross-type pooling trap the command-reachability
        // guard has to work around.
        var start = source.IndexOf("internal static void SetActive(", StringComparison.Ordinal);
        Assert.True(start >= 0, "SetActive not found — this check is not reading what it thinks it is.");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not delimit the SetActive body.");
        var body = source[start..end];

        var gated = typeof(SysManager.ViewModels.MainWindowViewModel).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "SysManager.ViewModels"
                     && t.Name.EndsWith("ViewModel", StringComparison.Ordinal)
                     && t.GetProperty("IsActive") is not null)
            // Row-level view models are not tabs, so the shell never navigates to them.
            .Where(t => t.Name != "AudioSessionRowViewModel")
            .ToList();

        // Vacuity floor: if a rename made the reflection find nothing, every assertion below would pass
        // while checking nothing at all.
        Assert.True(gated.Count >= 5,
            $"Expected at least 5 view models with an IsActive flag, found {gated.Count} — " +
            "the reflection filter is probably no longer matching.");

        var missing = gated
            .Where(t => !body.Contains(t.Name, StringComparison.Ordinal))
            .Select(t => t.Name)
            .ToList();

        Assert.True(missing.Count == 0,
            "These view models declare an IsActive flag but MainWindowViewModel.SetActive never sets " +
            "it, so their poll keeps running while the tab is hidden. Add a case arm:\n  " +
            string.Join("\n  ", missing));
    }

    /// <summary>
    /// <c>MainWindow.OnClosing</c> must request an application shutdown, not merely close the window.
    /// </summary>
    /// <remarks>
    /// <c>App</c> sets <c>ShutdownMode.OnExplicitShutdown</c> so SysManager can live in the notification
    /// area, which means closing the last window does NOT end the process. The Exit branch used to fall
    /// through to <c>base.OnClosing</c> with no <c>Shutdown</c> call, leaving the app running with no
    /// window and no tray icon (the icon is disposed in <c>App.OnExit</c>, which nothing had triggered),
    /// still holding the single-instance mutex — so the next launch handed itself over to an invisible
    /// instance and quit, and because the answer is remembered that repeated on every launch (#1827).
    /// <para>
    /// The decision itself is unit-tested through <c>CloseDecision</c>; this pins the one part that
    /// cannot be: that the window's own code actually performs the shutdown. Source-level because
    /// <c>OnClosing</c> is protected WPF code-behind a headless test cannot invoke.
    /// </para>
    /// </remarks>
    [Fact]
    public void ClosingTheWindowToExit_RequestsAnApplicationShutdown()
    {
        var source = File.ReadAllText(Path.Combine(FindAppProjectDir(), "MainWindow.xaml.cs"));

        var start = source.IndexOf("protected override void OnClosing(", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnClosing not found — this check is not reading what it thinks it is.");
        var end = source.IndexOf("protected override void OnClosed(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not delimit the OnClosing body.");
        var body = source[start..end];

        // Vacuity floor: the body must still contain the branch this is about, otherwise the assertion
        // below could pass against an OnClosing that no longer decides anything.
        Assert.Contains("CloseAction", body);
        Assert.Contains("Shutdown()", body);
    }

    /// <summary>
    /// A <c>Dispose</c> that disposes a <see cref="CancellationTokenSource"/> must cancel it first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CancellationTokenSource.Dispose()</c> does NOT cancel — that is documented behaviour, and it
    /// is easy to read a lone <c>_cts?.Dispose()</c> as "stop the work" when it means the opposite. 28
    /// sources across 23 view models had that shape, so closing a tab (or the whole app) left whatever
    /// they started running: <c>FileShredderViewModel</c> kept overwriting files after teardown,
    /// <c>DeepCleanupViewModel</c> and <c>BrowserCleanerViewModel</c> kept deleting, and
    /// <c>DashboardViewModel</c>'s one-click Tune-Up kept mutating system state. Every one is reachable
    /// at exit, because <c>MainWindowViewModel.Dispose</c> disposes each nav item.
    /// </para>
    /// <para>
    /// Keyed on the FIELD DECLARATION, not on what the Dispose body mentions. Scanning the body alone
    /// gets the answer wrong in both directions — it misses a class whose field is declared elsewhere and
    /// it flags one that cancels through a differently-shaped call. I made both mistakes by hand while
    /// triaging this, which is the argument for the check existing at all.
    /// </para>
    /// <para>
    /// Accepts any cancel on the field (<c>_cts.Cancel()</c>, <c>_cts?.Cancel()</c>, or a
    /// try/catch-wrapped one as <c>DnsHostsViewModel</c> uses), because the requirement is that
    /// cancellation happens, not that it is spelled a particular way.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryDisposedCancellationSource_IsCancelledFirst()
    {
        var vmDir = Path.Combine(FindAppProjectDir(), "ViewModels");
        var offenders = new List<string>();
        var checkedFields = 0;

        foreach (var file in Directory.GetFiles(vmDir, "*.cs"))
        {
            var source = File.ReadAllText(file);

            var start = source.IndexOf("protected override void Dispose(bool", StringComparison.Ordinal);
            if (start < 0) continue;
            var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
            if (end < 0) continue;
            var body = source[start..end];

            // Fields this type declares as a cancellation source, however they are initialised.
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in CancellationFieldDeclaration().Matches(source))
                fields.Add(m.Groups[1].Value);
            foreach (Match m in CancellationFieldAssignment().Matches(source))
                fields.Add(m.Groups[1].Value);

            foreach (var field in fields)
            {
                if (!body.Contains($"{field}?.Dispose()", StringComparison.Ordinal)
                    && !body.Contains($"{field}.Dispose()", StringComparison.Ordinal))
                    continue;   // not disposed here — nothing to require

                checkedFields++;
                if (!body.Contains($"{field}?.Cancel()", StringComparison.Ordinal)
                    && !body.Contains($"{field}.Cancel()", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)} · {field}");
            }
        }

        // Vacuity floor: if the field-detection regexes stopped matching, every assertion here would
        // pass while inspecting nothing.
        Assert.True(checkedFields >= 25,
            $"Expected at least 25 disposed cancellation sources, found {checkedFields} — " +
            "the detection is probably no longer matching the field declarations.");

        Assert.True(offenders.Count == 0,
            "These cancellation sources are disposed without being cancelled, so work already in "
            + "flight keeps running after teardown — Dispose() does not cancel. Add a Cancel() before "
            + "the Dispose():\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>A field declared as a cancellation source, e.g. <c>private CancellationTokenSource? _cts;</c>.</summary>
    [GeneratedRegex(@"CancellationTokenSource\??\s+(_[A-Za-z]\w*)", RegexOptions.Compiled)]
    private static partial Regex CancellationFieldDeclaration();

    /// <summary>A field assigned a new cancellation source, for types that declare it via <c>var</c>-like shapes.</summary>
    [GeneratedRegex(@"(_[A-Za-z]\w*)\s*=\s*new CancellationTokenSource", RegexOptions.Compiled)]
    private static partial Regex CancellationFieldAssignment();

    /// <summary>
    /// A type that disposes a <see cref="SemaphoreSlim"/> must track its own disposal, because an
    /// operation already inside the gate resumes after teardown: entering a disposed gate throws, and
    /// releasing one throws out of a <c>finally</c> block, turning a clean teardown into an unhandled
    /// exception. The sibling of <see cref="EveryDisposedCancellationSource_IsCancelledFirst"/>, and the
    /// same class of half-migration — the correct shape existed in three types while six others, two of
    /// them view models disposed on every tab close, had no flag at all.
    /// </summary>
    [Fact]
    public void EveryTypeThatDisposesAGate_TracksItsOwnDisposal()
    {
        var appDir = FindAppProjectDir();
        var offenders = new List<string>();
        var checkedTypes = 0;

        foreach (var file in Directory.GetFiles(Path.Combine(appDir, "Services"), "*.cs")
                     .Concat(Directory.GetFiles(Path.Combine(appDir, "ViewModels"), "*.cs")))
        {
            var source = File.ReadAllText(file);

            var fields = GateFieldDeclaration().Matches(source)
                .Select(m => m.Groups[1].Value)
                .Where(f => source.Contains($"{f}.Dispose()", StringComparison.Ordinal))
                .ToList();
            if (fields.Count == 0) continue;

            checkedTypes++;

            // Either its own flag, or the one ViewModelBase exposes for exactly this purpose.
            if (!source.Contains("_disposed", StringComparison.Ordinal)
                && !source.Contains("IsDisposed", StringComparison.Ordinal))
                offenders.Add($"{Path.GetFileName(file)} · disposes {string.Join(", ", fields)} with no disposal flag");
        }

        // Vacuity floor: if the declaration regex stopped matching, this would inspect nothing and pass.
        Assert.True(checkedTypes >= 10,
            $"Expected at least 10 types that dispose a gate, found {checkedTypes} — the detection is "
            + "probably no longer matching the field declarations.");

        Assert.True(offenders.Count == 0,
            "These types dispose a SemaphoreSlim without tracking disposal, so an operation still inside "
            + "the gate throws on release after teardown. Guard the wait and the release against a "
            + "disposal flag (ViewModelBase.IsDisposed for view models):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>A field declared as a gate, e.g. <c>private readonly SemaphoreSlim _gate = new(1, 1);</c>.</summary>
    [GeneratedRegex(@"SemaphoreSlim\??\s+(_[A-Za-z]\w*)", RegexOptions.Compiled)]
    private static partial Regex GateFieldDeclaration();

    /// <summary>
    /// Every place that sends a user somewhere to ask a question must deep-link the Q&amp;A category,
    /// never the Discussions root. Each release auto-posts an announcement, so the root is a wall of
    /// changelogs; four separate surfaces (the in-app button, SUPPORT.md, README.md and the issue
    /// chooser) all pointed there, and each was written independently — exactly the drift a fitness
    /// function catches better than review does.
    /// </summary>
    [Fact]
    public void EverySupportRoute_DeepLinksTheQuestionCategory_NotTheDiscussionsRoot()
    {
        var root = FindRepoRoot();
        string[] surfaces = ["SUPPORT.md", "README.md", Path.Combine(".github", "ISSUE_TEMPLATE", "config.yml")];

        var offenders = new List<string>();
        var deepLinks = 0;

        foreach (var relative in surfaces)
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), $"{relative} not found at {path} — the guard would pass vacuously");

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var hit in DiscussionsRootLink().Matches(lines[i]).Cast<Match>())
                    offenders.Add($"{relative}:{i + 1}  {hit.Value}");

                deepLinks += QuestionCategoryLink().Matches(lines[i]).Count;
            }
        }

        // Vacuity floor: the three doc surfaces plus the view-model must actually carry the deep link,
        // so an accidental find-and-delete can't turn this into a test that asserts nothing.
        Assert.True(deepLinks >= 3,
            $"expected the Q&A deep link on all three doc surfaces, found {deepLinks} — the guard has gone vacuous");
        Assert.Equal($"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/discussions/categories/q-a",
            SysManager.ViewModels.AboutViewModel.QuestionsUrl);

        Assert.True(offenders.Count == 0,
            "these support routes still point at the Discussions root instead of /discussions/categories/q-a:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>A link to the Discussions root — <c>/discussions</c> not followed by a category path.</summary>
    [GeneratedRegex(@"/discussions(?![/\w-])", RegexOptions.Compiled)]
    private static partial Regex DiscussionsRootLink();

    /// <summary>A link that correctly deep-links the Q&amp;A category.</summary>
    [GeneratedRegex(@"/discussions/categories/q-a", RegexOptions.Compiled)]
    private static partial Regex QuestionCategoryLink();

    /// <summary>
    /// Every discussion-category deep link must name a category that exists. GitHub answers an unknown
    /// slug with a 404, and the docs are the one place a typo would go unnoticed — nothing compiles them.
    /// The list mirrors the repository's configured categories.
    /// </summary>
    [Fact]
    public void EveryDiscussionCategoryLink_NamesACategoryThatExists()
    {
        string[] slugs = ["announcements", "general", "ideas", "polls", "q-a", "show-and-tell"];
        var root = FindRepoRoot();

        var offenders = new List<string>();
        var links = 0;

        foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly)
                     .Concat(Directory.EnumerateFiles(Path.Combine(root, ".github", "ISSUE_TEMPLATE"), "*.yml")))
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var hit in CategoryLink().Matches(lines[i]).Cast<Match>())
                {
                    links++;
                    var slug = hit.Groups[1].Value;
                    if (!slugs.Contains(slug))
                        offenders.Add($"{Path.GetFileName(path)}:{i + 1}  unknown category '{slug}'");
                }
            }
        }

        Assert.True(links >= 4, $"expected the category deep links to be present, found {links}");
        Assert.True(offenders.Count == 0,
            "these links name a discussion category that does not exist (GitHub answers 404):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>A discussion-category deep link, capturing the slug.</summary>
    [GeneratedRegex(@"/discussions/categories/([a-z0-9-]+)", RegexOptions.Compiled)]
    private static partial Regex CategoryLink();

    /// <summary>
    /// A field a search box matches against must be visible somewhere in that tab's view. Otherwise the
    /// user is invited to filter by text the app never shows them — and it hides a whole unreachable
    /// field: the Process Manager loaded a plain-English description and a category from its 108-entry
    /// database, matched both in the search, and rendered neither, showing the raw exe FileDescription
    /// instead. The searchable-but-invisible field is the quiet signature of that defect class.
    /// </summary>
    [Fact]
    public void EverySearchableField_IsVisibleInItsView()
    {
        var appDir = FindAppProjectDir();
        var source = File.ReadAllText(Path.Combine(appDir, "ViewModels", "ProcessManagerViewModel.cs"));
        var xaml = File.ReadAllText(Path.Combine(appDir, "Views", "ProcessManagerView.xaml"));

        // The filter enumerates its fields in one span; read them from there rather than restating them,
        // so adding a fourth searchable field is covered automatically.
        var span = SearchableFieldSpan().Match(source);
        Assert.True(span.Success,
            "could not find the Process Manager's searchable-field list — if the filter was rewritten, "
            + "update this guard rather than deleting it");

        var fields = span.Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Replace("p.", "", StringComparison.Ordinal))
            .ToList();

        Assert.True(fields.Count >= 3, $"expected the filter's field list, parsed {fields.Count}");

        var invisible = fields
            .Where(f => !xaml.Contains($"Binding {f}", StringComparison.Ordinal))
            .ToList();

        Assert.True(invisible.Count == 0,
            "the Process Manager search matches these fields but its view renders none of them, so a "
            + "user can filter by text they were never shown:\n  " + string.Join("\n  ", invisible));
    }

    /// <summary>The Process Manager's searchable-field span, capturing the field list.</summary>
    [GeneratedRegex(@"ReadOnlySpan<string\?>\s+fields\s*=\s*\[([^\]]+)\]", RegexOptions.Compiled)]
    private static partial Regex SearchableFieldSpan();

    /// <summary>
    /// Every commit prefix the release workflow treats as releasing must be offered by the PR template,
    /// and every prefix the template offers must be one the project actually recognises.
    /// <para>The template's "Type of change" list omitted <c>test:</c> and <c>refactor:</c> — 41 and 15
    /// such commits exist on main, and CONTRIBUTING names both — so a contributor filling it in honestly
    /// had no box to tick. That matters more than tidiness because the prefix is what decides whether the
    /// merge publishes a release, and the rest of the checklist branches on that: a releasing PR must bump
    /// the version and add a CHANGELOG entry, a non-releasing one must do neither or CI's version gate
    /// fails it. The list and the workflow are one contract written in two places.</para>
    /// </summary>
    [Fact]
    public void ThePullRequestTemplate_OffersEveryCommitPrefixTheWorkflowUnderstands()
    {
        var root = FindRepoRoot();
        var lines = File.ReadAllLines(Path.Combine(root, ".github", "PULL_REQUEST_TEMPLATE.md"));
        var autoRelease = File.ReadAllText(Path.Combine(root, ".github", "workflows", "auto-release.yml"));

        // The prefixes auto-release maps to a bump, read from the workflow rather than restated here.
        var releasing = ReleasingPrefixAlternation().Matches(autoRelease)
            .SelectMany(m => m.Groups[1].Value.Split('|'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["feat", "fix"], releasing.Order(StringComparer.Ordinal));

        var offered = lines
            .Select(l => TemplatePrefix().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(offered.Count >= 6,
            $"only {offered.Count} prefixes were parsed out of the PR template — fix this guard rather "
            + "than trusting its pass.");

        // The non-releasing prefixes CONTRIBUTING tells contributors to use. Whatever the template
        // offers must come from one of these two sets; anything else is a box mapping to no behaviour.
        string[] silent = ["docs", "test", "refactor", "ci", "chore"];
        var known = releasing.Concat(silent).ToHashSet(StringComparer.Ordinal);

        var missing = known.Where(p => !offered.Contains(p)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "these commit prefixes are used on main and documented in CONTRIBUTING, but the PR template "
            + $"offers no box for them, so a contributor cannot declare one: {string.Join(", ", missing)}");

        var unknown = offered.Where(p => !known.Contains(p)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0,
            "the PR template offers these prefixes, but neither auto-release nor CONTRIBUTING knows "
            + $"them: {string.Join(", ", unknown)}");

        // A releasing box must SAY it releases, because the checklist below it branches on exactly that.
        foreach (var prefix in releasing.Order(StringComparer.Ordinal))
        {
            var silentBoxes = lines
                .Where(l => TemplatePrefix().IsMatch(l)
                            && TemplatePrefix().Match(l).Groups[1].Value == prefix
                            && !l.Contains("releases", StringComparison.Ordinal))
                .ToList();
            Assert.True(silentBoxes.Count == 0,
                $"a `{prefix}:` box publishes a release when merged, but does not say so:\n  "
                + string.Join("\n  ", silentBoxes));
        }
    }

    /// <summary>
    /// The PR checklist must ask for the things CI hard-fails on, and must not ask a non-releasing PR
    /// for the things that make CI fail.
    /// <para>It asked for "CHANGELOG updated" unconditionally. Following that on a <c>docs:</c> or
    /// <c>test:</c> PR turns the build red — the version gate requires the newest CHANGELOG heading to
    /// equal the csproj version, and a non-releasing PR leaves that version alone. Meanwhile the two
    /// checks that break most PRs, <c>dotnet format</c> and the version bump, were not mentioned at all:
    /// a checklist that omits the real gates and demands a red one is worse than none, because it is
    /// followed in good faith.</para>
    /// </summary>
    [Fact]
    public void ThePullRequestChecklist_AsksForWhatCiEnforces()
    {
        var root = FindRepoRoot();
        var lines = File.ReadAllLines(Path.Combine(root, ".github", "PULL_REQUEST_TEMPLATE.md"));
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        // Anchor on the CI steps themselves, so a renamed gate fails here rather than silently
        // invalidating the expectations below.
        foreach (var step in new[]
                 {
                     "name: Check formatting",
                     "name: Check version consistency",
                     "name: Check the version is the one the merge will tag",
                     "name: Check the newest CHANGELOG entry has a plain-English lead"
                 })
        {
            Assert.Contains(step, ci, StringComparison.Ordinal);
        }

        var items = lines
            .Select((text, index) => (Text: text.Trim(), Line: index + 1))
            .Where(item => item.Text.StartsWith("- [ ] ", StringComparison.Ordinal))
            .ToList();
        Assert.True(items.Count >= 12,
            $"only {items.Count} checklist items were parsed — fix this guard rather than trusting it.");

        // Where the checklist splits into the release-only section. Everything from that heading down is
        // explicitly scoped to fix:/feat:, which is what makes asking for a CHANGELOG correct there.
        var releaseSection = Array.FindIndex(
            lines,
            l => l.StartsWith("### ", StringComparison.Ordinal)
                 && l.Contains("fix:", StringComparison.Ordinal)
                 && l.Contains("feat:", StringComparison.Ordinal)) + 1;
        Assert.True(releaseSection > 0,
            "the template has no release-only checklist section — a CHANGELOG or version-bump item "
            + "outside one applies to every PR, including the ones CI fails for doing it.");

        foreach (var (needle, what) in new[]
                 {
                     ("CHANGELOG", "the CHANGELOG entry"),
                     ("Version", "the version bump")
                 })
        {
            var stray = items
                .Where(item => item.Line < releaseSection
                               && item.Text.Contains(needle, StringComparison.Ordinal))
                .Select(item => $"line {item.Line}: {item.Text}")
                .ToList();
            Assert.True(stray.Count == 0,
                $"{what} is asked of every PR, but on a docs:/test:/refactor:/ci:/chore: PR doing it "
                + "fails CI's version gate. Move it under the release-only heading:\n  "
                + string.Join("\n  ", stray));

            Assert.Contains(items, item => item.Line > releaseSection
                                           && item.Text.Contains(needle, StringComparison.Ordinal));
        }

        // The format gate fails more PRs than any other check and a clean build does not catch it, so
        // the checklist has to name it — and CONTRIBUTING has to show the command.
        Assert.Contains(items, item => item.Text.Contains("dotnet format", StringComparison.Ordinal));
        Assert.Contains(
            "dotnet format",
            File.ReadAllText(Path.Combine(root, "CONTRIBUTING.md")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every private-reporting route must point at a channel that exists. SECURITY.md and
    /// CODE_OF_CONDUCT.md both told reporters to email "the address on the GitHub profile" — that profile
    /// publishes no email, so the one page a security reporter reads named a dead end. A vulnerability
    /// that cannot be reported privately tends to be reported publicly, or not at all.
    /// </summary>
    [Fact]
    public void EveryPrivateReportingRoute_NamesAChannelThatExists()
    {
        var root = FindRepoRoot();
        string[] surfaces = ["SECURITY.md", "CODE_OF_CONDUCT.md", "SUPPORT.md"];

        var offenders = new List<string>();
        var advisoryLinks = 0;

        foreach (var relative in surfaces)
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), $"{relative} not found — the guard would pass vacuously");

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                advisoryLinks += AdvisoryLink().Matches(lines[i]).Count;

                // "the email listed on their GitHub profile", "contact the maintainer by email" and
                // friends: any instruction to email that names no actual address routes nowhere.
                foreach (var hit in EmailTheMaintainer().Matches(lines[i]).Cast<Match>())
                    offenders.Add($"{relative}:{i + 1}  {hit.Value.Trim()}");
            }
        }

        // Vacuity floor: the advisory route must be present on the pages that replaced the email one.
        Assert.True(advisoryLinks >= 3,
            $"expected the private-advisory link on the reporting pages, found {advisoryLinks} — the "
            + "guard has gone vacuous");

        Assert.True(offenders.Count == 0,
            "these lines tell a reporter to email the maintainer, but no address is published anywhere "
            + "(the GitHub profile has none), so the route is a dead end. Point at a private security "
            + $"advisory instead:\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>
    /// Every source file carries the three-line attribution header the PR template asks for.
    /// <para>All 688 of them did already — but only because it was remembered every time, on a checklist
    /// item whose exact shape was written down nowhere public: a contributor reading "Author headers on
    /// all new/modified files" had to infer the format from a neighbouring file. CONTRIBUTING now shows
    /// it, and this asserts it, so the instruction and the reality cannot drift apart.</para>
    /// </summary>
    [Fact]
    public void EverySourceFile_CarriesTheAuthorHeader()
    {
        const string attribution = "Author: laurentiu021 · https://github.com/laurentiu021/SystemManager";
        var solution = Path.Combine(FindRepoRoot(), "SysManager");

        var offenders = new List<string>();
        var scanned = 0;

        foreach (var path in Directory
                     .EnumerateFiles(solution, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                 || p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                     .Where(p => !Path.GetRelativePath(solution, p)
                         .Split(Path.DirectorySeparatorChar)
                         .Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                                         || segment.Equals("bin", StringComparison.OrdinalIgnoreCase))))
        {
            scanned++;

            // The header is the first thing in the file, so only the opening lines are read: a stray
            // match further down (a URL in a comment, say) must not satisfy the contract.
            var opening = string.Join('\n', File.ReadLines(path).Take(5));
            if (!opening.Contains("SysManager ·", StringComparison.Ordinal)
                || !opening.Contains(attribution, StringComparison.Ordinal)
                || !opening.Contains("License: MIT", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(solution, path));
            }
        }

        // Vacuity floor: the four projects hold ~690 files, so a scan that saw a handful means the
        // enumeration broke rather than the codebase being clean.
        Assert.True(scanned >= 600,
            $"only {scanned} source files were scanned — fix this guard rather than trusting its pass.");

        Assert.True(offenders.Count == 0,
            "these files are missing the attribution header the PR template requires. Copy the block "
            + "from the top of a neighbouring file; CONTRIBUTING.md shows the exact shape for .cs and "
            + $".xaml:\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>The <c>feat|fix</c> alternation auto-release matches to decide a bump.</summary>
    [GeneratedRegex(@"\^\((feat\|fix)\)", RegexOptions.Compiled)]
    private static partial Regex ReleasingPrefixAlternation();

    /// <summary>A commit prefix offered by a PR-template checkbox, e.g. <c>(`feat:`)</c>.</summary>
    [GeneratedRegex(@"^- \[ \].*\(`([a-z]+):`\)", RegexOptions.Compiled)]
    private static partial Regex TemplatePrefix();

    /// <summary>A link that opens a private security advisory.</summary>
    [GeneratedRegex(@"/security/advisories/new", RegexOptions.Compiled)]
    private static partial Regex AdvisoryLink();

    /// <summary>An instruction to email the maintainer that names no address.</summary>
    [GeneratedRegex(
        @"email(?:ing)?\s+(?:the\s+)?maintainer|(?:e-?mail|address)\s+(?:listed\s+)?on\s+(?:their|the)\s+GitHub\s+profile",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex EmailTheMaintainer();

    /// <summary>
    /// Every model property must be reachable: written by something, or shown by something. A property
    /// that is neither is a promise the app cannot keep — <c>BlockedApp.FullPath</c> was permanently empty
    /// because an IFEO key records only the executable name, and <c>ProcessEntry.UserName</c> was never
    /// assigned at all. Both had a passing unit test asserting their default value, which is what let them
    /// look exercised while displaying nothing.
    /// <para>Setter-only is fine (a value the app consumes in C#), and display-only is fine (a computed
    /// property). Neither, with no XAML binding either, is dead.</para>
    /// </summary>
    [Fact]
    public void EveryModelProperty_IsEitherWrittenOrShown()
    {
        var appDir = FindAppProjectDir();
        var modelsDir = Path.Combine(appDir, "Models");

        var xaml = string.Join('\n', Directory
            .EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));

        var sources = Directory
            .EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => f, File.ReadAllText);

        var dead = new List<string>();
        var checkedProperties = 0;

        foreach (var (path, source) in sources.Where(kv => kv.Key.StartsWith(modelsDir, StringComparison.Ordinal)))
        {
            var typeName = TypeDeclaration().Match(source).Groups[1].Value;
            if (typeName.Length == 0) continue;

            foreach (var m in ObservablePropertyField().Matches(source).Cast<Match>())
            {
                var field = m.Groups[1].Value;
                var property = char.ToUpperInvariant(field[0]) + field[1..];
                checkedProperties++;

                if (xaml.Contains(property, StringComparison.Ordinal)) continue;
                if (Regex.IsMatch(source, $@"\b{Regex.Escape(property)}\b")) continue;

                // Require the declaring type's name in the same file, so a same-named property on another
                // model (FriendlyEventEntry also has a UserName) cannot make this one look alive.
                var referenced = sources.Any(kv => kv.Key != path
                    && kv.Value.Contains(typeName, StringComparison.Ordinal)
                    && Regex.IsMatch(kv.Value, $@"\b{Regex.Escape(property)}\b"));
                if (referenced) continue;

                dead.Add($"{typeName}.{property} ({Path.GetFileName(path)})");
            }
        }

        Assert.True(checkedProperties >= 40,
            $"only {checkedProperties} model properties were inspected — the detection is probably no "
            + "longer matching the [ObservableProperty] declarations.");

        Assert.True(dead.Count == 0,
            "these model properties are never written and never shown, so they can only ever present an "
            + "empty value to the user. Populate them or remove them:\n  " + string.Join("\n  ", dead));
    }

    /// <summary>A class or record declaration, capturing the type name.</summary>
    [GeneratedRegex(@"(?:class|record)\s+(\w+)", RegexOptions.Compiled)]
    private static partial Regex TypeDeclaration();

    /// <summary>An <c>[ObservableProperty]</c> backing field, capturing the field name without its underscore.</summary>
    [GeneratedRegex(@"\[ObservableProperty\][^\n]*\n?\s*(?:private|internal)\s+[\w\?<>,\[\]\. ]+?\s+_(\w+)\s*[;=]",
        RegexOptions.Compiled)]
    private static partial Regex ObservablePropertyField();

    /// <summary>
    /// Every issue template that asks which tab is affected must offer the real tabs. Two of the three
    /// templates were still offering an 18-entry list from when the app had roughly that many tabs, with
    /// names that no longer matched the sidebar ("Cleanup" for "Quick Cleanup") and one entry — "Network"
    /// — that is a nav group, not a tab. A reporter could not name 41 of the 58 tabs, so reports arrived
    /// mis-labelled or unlabelled. Nothing compiles a YAML dropdown, so only a test catches the drift.
    /// </summary>
    [Theory]
    [InlineData("bug_report.yml", "tab")]
    [InlineData("feature_request.yml", "scope")]
    [InlineData("general_issue.yml", "tab")]
    public void EveryIssueTemplateTabList_OffersTheRealTabs(string template, string dropdownId)
    {
        var labels = SidebarTabLabels();
        Assert.True(labels.Count >= 50,
            $"only {labels.Count} tab labels were parsed from MainWindowViewModel — the guard is vacuous");

        var options = DropdownOptions(
            Path.Combine(FindRepoRoot(), ".github", "ISSUE_TEMPLATE", template), dropdownId);
        Assert.NotEmpty(options);

        // Every real tab must be offerable. Extra options are fine: each template ends with its own
        // catch-alls ("Not sure", "New tab / cross-cutting"), which are deliberate, not tab names.
        var missing = labels.Where(l => !options.Contains(l)).ToList();
        Assert.True(missing.Count == 0,
            $"{template} ({dropdownId}) cannot describe {missing.Count} of the {labels.Count} tabs:\n  "
            + string.Join("\n  ", missing));

        // And no option may name a tab that does not exist — a stale name is as misleading as a gap.
        string[] catchAlls = ["Multiple / general UI", "Not sure", "New tab / cross-cutting"];
        var unknown = options.Where(o => !labels.Contains(o) && !catchAlls.Contains(o)).ToList();
        Assert.True(unknown.Count == 0,
            $"{template} ({dropdownId}) offers options that are not tabs (nor known catch-alls):\n  "
            + string.Join("\n  ", unknown));
    }

    /// <summary>
    /// The version field of an issue template must not pin an example release. Both templates showed
    /// <c>0.12.x</c> — roughly 130 releases stale — so a reporter copying the placeholder filed against
    /// a version that never shipped, in the field used for triage. Writing today's number in only resets
    /// the clock, so the rule is that no version literal belongs there at all.
    /// </summary>
    [Theory]
    [InlineData("bug_report.yml")]
    [InlineData("general_issue.yml")]
    public void TheIssueTemplateVersionField_PinsNoExampleRelease(string template)
    {
        var path = Path.Combine(FindRepoRoot(), ".github", "ISSUE_TEMPLATE", template);
        Assert.True(File.Exists(path), $"{template} not found — the guard would pass vacuously");

        var lines = File.ReadAllLines(path);
        var field = Array.FindIndex(lines, l => l.Trim() == "id: version");
        Assert.True(field >= 0, $"{template} has no version field — the guard is vacuous");

        var pinned = new List<string>();
        for (var i = field; i < lines.Length; i++)
        {
            if (i > field && lines[i].TrimStart().StartsWith("- type:", StringComparison.Ordinal)) break;

            var m = SemanticVersion().Match(lines[i]);
            if (m.Success) pinned.Add($"{template}:{i + 1}  {m.Groups[1].Value}");
        }

        Assert.True(pinned.Count == 0,
            "the version field pins an example release, which goes stale on the next release:\n  "
            + string.Join("\n  ", pinned));
    }

    /// <summary>A three-part version number.</summary>
    [GeneratedRegex(@"\b(\d+\.\d+\.\d+)\b", RegexOptions.Compiled)]
    private static partial Regex SemanticVersion();

    /// <summary>The tab labels exactly as the sidebar registers them.</summary>
    private static HashSet<string> SidebarTabLabels()
    {
        var source = File.ReadAllText(
            Path.Combine(FindAppProjectDir(), "ViewModels", "MainWindowViewModel.cs"));

        // Both registration helpers take (id, label, …); the label is the second string argument.
        return NavRegistration().Matches(source)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A nav registration, capturing the user-visible label.</summary>
    [GeneratedRegex(@"(?:Tab<\w+>|EagerItem)\(\s*""[\w-]+""\s*,\s*""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex NavRegistration();

    /// <summary>
    /// Only the tabs with a stated reason may be built at startup.
    /// <para>Every tab view model used to be constructed in <c>MainWindowViewModel</c>'s constructor, so
    /// launching the app ran ~40 constructors — several of which start a scan or a timer — before the
    /// first frame. The lazy <c>Tab&lt;TVm&gt;</c> factory fixed that by resolving each view model on
    /// first open, and three tabs were documented as legitimate exceptions: Dashboard (the initially
    /// selected tab), DarkMode (owns the always-on theme schedule) and About (its update check feeds the
    /// shell banner). DarkMode and About are resolved directly in the constructor, not through the nav
    /// table, so only Dashboard appears here.</para>
    /// <para>The four network tabs nevertheless stayed on the eager path, and the justification comment
    /// was widened to say "network tabs" instead of the tabs being made lazy — so
    /// <c>SpeedTestViewModel</c>'s constructor read its history file from disk at every launch whether or
    /// not anyone opened Speed Test. Worse, each was built with <c>new</c> while the container already
    /// registered it as a singleton, so the app carried two instances of each and the DI registrations
    /// were dead.</para>
    /// <para>A comment cannot hold that line, so this asserts it. Adding a new eager tab fails here,
    /// which is the intended prompt to either justify it in the list below or use <c>Tab&lt;TVm&gt;</c>.</para>
    /// </summary>
    [Fact]
    public void OnlyTheJustifiedTabs_AreBuiltAtStartup()
    {
        var source = File.ReadAllText(
            Path.Combine(FindAppProjectDir(), "ViewModels", "MainWindowViewModel.cs"));

        // Scoped to the runtime nav table. BuildDesignerGraph below it constructs everything eagerly by
        // design (there is no container in the designer/test path), so including it would flag the
        // wrong thing.
        // The end marker is the DECLARATION, not the bare method name: BuildDesignerGraph() also appears
        // as a CALL in the constructor, ABOVE the nav table, so matching the bare name yields end < start
        // and an empty slice — a guard that passes while reading nothing. The asserts below make that
        // failure loud instead of silent.
        var start = source.IndexOf("private NavGroup[] BuildNavGroups()", StringComparison.Ordinal);
        var end = source.IndexOf("private Dictionary<Type, object> BuildDesignerGraph()",
                                 StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start,
            $"Could not locate the nav table in MainWindowViewModel (start={start}, end={end}) — "
            + "fix this guard, do not trust its result.");
        var navTable = source[start..end];
        Assert.Contains("Tab<", navTable, StringComparison.Ordinal);

        // The three exceptions the shell's own constructor documents, and the reason each earns it.
        // Anything else appearing here is the regression this test exists to catch.
        string[] justified =
        [
            "Dashboard",            // the initially selected tab — built immediately regardless
            "Dark Mode Scheduler",  // owns the always-on theme schedule poll; nothing else runs it
            "About",                // its constructor's update check feeds the shell's update banner
        ];

        var eager = EagerNavRegistration().Matches(navTable)
            .Select(m => m.Groups["label"].Value)
            .Where(label => !justified.Contains(label, StringComparer.Ordinal))
            .ToList();

        // Vacuity floor: if the regex or the slice stopped matching, this would pass while reading
        // nothing at all.
        var lazyCount = LazyNavRegistration().Matches(navTable).Count;
        Assert.True(lazyCount >= 30,
            $"Only {lazyCount} lazy tab registrations were seen — the guard is not reading the nav "
            + "table. Fix this test rather than trusting its pass.");

        Assert.True(eager.Count == 0,
            "These tabs are constructed at startup with no stated reason, so their constructors run on "
            + "every launch even when the user never opens them — the startup-herd regression the lazy "
            + $"Tab<TVm> factory exists to prevent:\n  {string.Join("\n  ", eager)}\n"
            + "Use Tab<TVm>(…) so the view model comes from the container on first open. If a tab "
            + "genuinely must exist at startup, add it to the justified list in this test WITH its reason.");
    }

    // An eager registration in the nav table. `inDevelopment: true` placeholders are excluded by the
    // negative lookahead: those carry a stub, so they cost nothing at startup.
    [GeneratedRegex(@"EagerItem\(\s*""[\w-]+""\s*,\s*""(?<label>[^""]+)""(?![^)]*inDevelopment)",
                    RegexOptions.Compiled)]
    private static partial Regex EagerNavRegistration();

    [GeneratedRegex(@"Tab<\w+>\(\s*""[\w-]+""\s*,\s*""[^""]+""", RegexOptions.Compiled)]
    private static partial Regex LazyNavRegistration();

    /// <summary>
    /// The options of one dropdown in an issue-form template, read line-wise. A full YAML parse is
    /// avoided deliberately: these files contain unquoted colons inside descriptions, which several
    /// parsers reject, and a guard that cannot read the file is worse than no guard.
    /// </summary>
    private static List<string> DropdownOptions(string path, string dropdownId)
    {
        Assert.True(File.Exists(path), $"{path} not found — the guard would pass vacuously");

        var lines = File.ReadAllLines(path);
        var options = new List<string>();
        var inDropdown = false;
        var inOptions = false;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("- type:", StringComparison.Ordinal))
            {
                inDropdown = false;
                inOptions = false;
            }

            if (line.Trim() == $"id: {dropdownId}") inDropdown = true;
            if (!inDropdown) continue;

            if (line.Trim() == "options:") { inOptions = true; continue; }
            if (!inOptions) continue;

            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                options.Add(trimmed[2..].Trim().Trim('"'));
            else if (trimmed.Length > 0)
                break;      // the dropdown's option list ended
        }

        return options;
    }

    /// <summary>
    /// Every in-page link in the docs must resolve. README is 1300+ lines long, so the contents list is
    /// the only practical way to navigate it — and a heading rename silently breaks an anchor, which
    /// renders as a link that quietly does nothing rather than as an error.
    /// <para>Originally scoped to README's contents block, which is why it never saw
    /// <c>[top of this README](#sysmanager)</c> further down the file: the h1 is "SysManager for
    /// Windows", so that anchor slugs to <c>#sysmanager-for-windows</c> and the link had been inert since
    /// it was written. Every in-page link on every doc page is now checked, in both directions.</para>
    /// </summary>
    [Fact]
    public void TheReadmeTableOfContents_HasNoDeadAnchors()
    {
        var root = FindRepoRoot();
        var lines = File.ReadAllLines(Path.Combine(root, "README.md"));

        // GitHub's anchor rule: lower-case, drop everything but word characters, spaces and hyphens,
        // then replace runs of whitespace with a single hyphen.
        var anchors = lines
            .Select(l => HeadingLine().Match(l))
            .Where(m => m.Success)
            .Select(m => Slug(m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var start = Array.FindIndex(lines, l => l.Trim() == "## Table of contents");
        Assert.True(start >= 0, "README.md has no '## Table of contents' section — the guard is vacuous");

        var dead = new List<string>();
        var entries = 0;
        for (var i = start + 1; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
        {
            var m = InPageLink().Match(lines[i]);
            if (!m.Success) continue;
            entries++;
            if (!anchors.Contains(m.Groups[1].Value))
                dead.Add($"README.md:{i + 1}  #{m.Groups[1].Value}");
        }

        // The whole doc set, not just README's contents block: a prose cross-reference breaks exactly the
        // same way and is the one nobody re-reads.
        var pages = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly))
        {
            pages++;
            var page = File.ReadAllLines(path);
            var pageAnchors = page
                .Select(l => HeadingLine().Match(l))
                .Where(m => m.Success)
                .Select(m => Slug(m.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal);
            // The h1 is an anchor too, and prose links back to the top of the page through it.
            foreach (var l in page.Where(l => l.StartsWith("# ", StringComparison.Ordinal)))
                pageAnchors.Add(Slug(l[2..]));

            for (var i = 0; i < page.Length; i++)
            {
                foreach (var hit in InPageLink().Matches(page[i]).Cast<Match>())
                {
                    var anchor = hit.Groups[1].Value;
                    if (!pageAnchors.Contains(anchor))
                        dead.Add($"{Path.GetFileName(path)}:{i + 1}  #{anchor}");
                }
            }
        }

        Assert.True(pages >= 6, $"expected the top-level doc pages, found {pages}");
        Assert.True(entries >= 10, $"expected the full contents list, found {entries} entries");
        Assert.True(dead.Count == 0,
            "these in-page links point at headings that do not exist, so they render as text that "
            + $"quietly does nothing when clicked:\n  {string.Join("\n  ", dead)}");
    }

    private static string Slug(string heading) =>
        WhitespaceRun().Replace(NonAnchorCharacter().Replace(heading.Trim().ToLowerInvariant(), string.Empty).Trim(), "-");

    /// <summary>A markdown heading of level 2 or deeper, capturing its text.</summary>
    [GeneratedRegex(@"^#{2,}\s+(.*)$", RegexOptions.Compiled)]
    private static partial Regex HeadingLine();

    /// <summary>An in-page markdown link, capturing the anchor.</summary>
    [GeneratedRegex(@"\]\(#([^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex InPageLink();

    /// <summary>Characters GitHub strips when building an anchor.</summary>
    [GeneratedRegex(@"[^\w\s-]", RegexOptions.Compiled)]
    private static partial Regex NonAnchorCharacter();

    /// <summary>The repository root — the docs the guards read are not copied to the test output.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SUPPORT.md"))
                && File.Exists(Path.Combine(dir.FullName, "README.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// A service that persists user data must write it atomically, via <c>AtomicFile</c>.
    /// <para><c>File.WriteAllText</c> and friends truncate the destination and then write into it, so
    /// an interrupted save leaves a torn file. That is not merely a corrupt-file risk here: several
    /// loaders catch <c>JsonException</c> and substitute an empty list at Debug level, so a torn save
    /// silently erases the user's activity history, speed-test history or presets rather than
    /// reporting anything. 17 call sites across 15 services did this.</para>
    /// <para>Exempt, by name and for a stated reason: a write whose target is itself a temp/backup
    /// path (already the atomic pattern), <c>HostsFileService</c> (the original hand-rolled
    /// implementation this helper generalises), <c>ProfileService.ExportToFileAsync</c> (writes a new
    /// file the user picked — there is no existing data to lose), and the two <c>.sha256</c> sidecars
    /// in the update path (derived values, regenerated on demand, not user data).</para>
    /// </summary>
    [Fact]
    public void EveryServiceThatPersistsUserData_WritesItAtomically()
    {
        var servicesDir = Path.Combine(FindAppProjectDir(), "Services");
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.GetFiles(servicesDir, "*.cs"))
        {
            var name = Path.GetFileName(file);
            if (name is "HostsFileService.cs") continue;

            var source = File.ReadAllText(file);
            foreach (var match in RawFileWrite().Matches(source).Cast<Match>())
            {
                scanned++;
                var target = match.Groups["target"].Value.Trim();

                // Writing to a temp/backup path IS the atomic pattern; the swap follows.
                if (TempOrBackupTarget().IsMatch(target)) continue;
                // Derived sidecars hold no user data. Matched on the variable name as well as the
                // literal, because UpdateService writes through `hashFile = target + ".sha256"`.
                if (HashSidecarTarget().IsMatch(target)) continue;
                if (name == "ProfileService.cs" && target == "path"
                    && source.Contains("ExportToFileAsync", StringComparison.Ordinal)
                    && match.Index > source.IndexOf("ExportToFileAsync", StringComparison.Ordinal)
                    && match.Index < source.IndexOf("ImportFromFileAsync", StringComparison.Ordinal))
                    continue;

                var line = source[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{name}:{line} writes {target} in place");
            }
        }

        // Vacuity floor: if the regex stopped matching, this would pass while inspecting nothing.
        Assert.True(scanned >= 5,
            $"Only {scanned} raw File.Write* calls were seen across Services — the detection is "
            + "broken, not the code. Fix this guard rather than trusting it.");

        Assert.True(offenders.Count == 0,
            "These services write user data in place, so an interrupted save leaves a torn file — and "
            + "the loaders treat an unparseable file as no data, silently discarding it. Use "
            + "AtomicFile.WriteAllText/WriteAllBytes (or the Async overloads) instead:\n  "
            + string.Join("\n  ", offenders));
    }

    // (?&lt;!Atomic) is load-bearing: "AtomicFile.WriteAllText" ENDS IN "File.WriteAllText", so without
    // the lookbehind this regex matches the fix as well as the defect and the guard fails on green.
    [GeneratedRegex(@"(?<!Atomic)\bFile\.Write(?:AllText|AllBytes|AllLines)(?:Async)?\s*\(\s*(?<target>[^,)]+)",
                    RegexOptions.CultureInvariant)]
    private static partial Regex RawFileWrite();

    [GeneratedRegex(@"tmp|temp|\.bak|backup", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TempOrBackupTarget();

    [GeneratedRegex(@"\.sha256|hashFile", RegexOptions.CultureInvariant)]
    private static partial Regex HashSidecarTarget();

    /// <summary>
    /// Every date formatted with a fixed SHAPE must name the culture that shape belongs to.
    /// <para>A custom pattern with no <c>IFormatProvider</c> formats through
    /// <c>CultureInfo.CurrentCulture</c>, which Windows sets from the user's regional settings — so
    /// the same code prints a different string, and on some installs a DIFFERENT DATE, per machine.
    /// Measured for 2026-08-04 13:45:30 with this app's own patterns:</para>
    /// <list type="bullet">
    /// <item><c>yyyy-MM-dd</c> → <c>2026-08-04</c> on en-US, <c>2569-08-04</c> on th-TH (Buddhist
    /// calendar), <c>1448-02-21</c> on ar-SA (Umm al-Qura) — the year AND the month change</item>
    /// <item><c>HH:mm</c> → <c>13:45</c> on en-US, <c>13.45</c> on fi-FI — the ':' is the culture's
    /// time separator, so an ISO-looking string is neither ISO nor lexicographically sortable, which
    /// is the only reason to choose that pattern over <c>ToString("g")</c></item>
    /// <item><c>yyyyMMdd_HHmmss</c> → <c>25690804_134530</c> on th-TH — and this one lands in a
    /// registry-backup FILENAME, so the file is stamped with a date that is not the date</item>
    /// </list>
    /// <para>Both shapes carry the defect and both are checked: <c>x.ToString("pattern")</c> and the
    /// interpolation hole <c>$"{x:pattern}"</c>. An interpolation hole cannot take a provider, so the
    /// fix there is to format inside the hole.</para>
    /// <para>Standard format specifiers (<c>"g"</c>, <c>"f"</c>, <c>"D"</c>) are deliberately NOT
    /// flagged: localizing those IS correct, and they have no fixed shape to preserve.</para>
    /// </summary>
    [Fact]
    public void EveryFixedShapeDateFormat_NamesItsCulture()
    {
        var appDir = FindAppProjectDir();
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                              StringComparison.Ordinal)) continue;

            var source = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (var match in CultureBlindDateFormat().Matches(source).Cast<Match>())
            {
                var pattern = match.Groups["pattern"].Value;

                // A Serilog output template is not C# interpolation — Serilog parses it and the
                // formatter is constructed with an explicit culture, so the hole never reaches
                // string.Format. Recognised by Serilog's own token syntax, which C# has no notion of.
                if (SerilogTemplateToken().IsMatch(source[match.Index..Math.Min(source.Length, match.Index + match.Length + 40)])) continue;

                scanned++;
                var line = source[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{name}:{line} formats \"{pattern}\" through CurrentCulture");
            }
        }

        // Vacuity floor: the codebase keeps ~35 culture-explicit date formats. If the pattern class
        // stopped being recognised this test would pass while inspecting nothing, so prove the
        // detector still sees the compliant calls before trusting a clean result.
        var compliant = Directory
            .EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                    StringComparison.Ordinal))
            .Sum(f => CultureExplicitDateFormat().Matches(File.ReadAllText(f)).Count);
        Assert.True(compliant >= 25,
            $"Only {compliant} culture-explicit date formats were found — the detection is broken, "
            + "not the code. Fix this guard rather than trusting it.");

        Assert.True(offenders.Count == 0,
            "These dates are formatted with a fixed shape but no culture, so CurrentCulture decides the "
            + "output: on a Thai or Saudi regional setting the year and month change, and on Finnish the "
            + "time separator does. Pass CultureInfo.InvariantCulture (and for an interpolation hole, "
            + "format inside the hole — a hole takes no provider):\n  "
            + string.Join("\n  ", offenders)
            + $"\n({scanned} culture-blind, {compliant} culture-explicit)");
    }

    // A fixed-shape date pattern is one naming a calendar/clock field: yyyy, MM, dd, HH, mm, ss.
    // Alternative 1 — .ToString("pattern") with no second argument.
    // Alternative 2 — an interpolation hole $"{value:pattern}", which cannot take a provider at all.
    // Numeric specifiers (F1, N0, X8) are out of scope here: the decimal-separator class is a
    // separate concern, and hex formatting consults no culture data.
    // Both alternatives reuse the name "pattern" — .NET merges same-named groups, so whichever
    // alternative matched reports through Groups["pattern"] and the caller needs no branch.
    [GeneratedRegex(@"\.ToString\(""(?<pattern>[^""]*(?:yyyy|HH|mm:ss|MM-dd|dd MMM)[^""]*)""\s*\)"
                    + @"|\{[^{}""]{1,60}:(?<pattern>[^{}]*(?:yyyy|HH:mm|mm:ss)[^{}]*)\}",
                    RegexOptions.CultureInvariant)]
    private static partial Regex CultureBlindDateFormat();

    [GeneratedRegex(@"\.ToString\(""[^""]*(?:yyyy|HH|mm:ss|MM-dd|dd MMM)[^""]*""\s*,\s*(?:System\.Globalization\.)?CultureInfo\.",
                    RegexOptions.CultureInvariant)]
    private static partial Regex CultureExplicitDateFormat();

    // Serilog's {Level:u3} / {Message:lj} tokens have no C# equivalent, so their presence right after
    // the match identifies an output template rather than an interpolated string.
    [GeneratedRegex(@"\{(?:Level:u3|Message:lj|NewLine|Exception)\}", RegexOptions.CultureInvariant)]
    private static partial Regex SerilogTemplateToken();

    /// <summary>
    /// Every number formatted with a decimal or grouped specifier must name its culture, so it agrees
    /// with <c>FormatHelper</c>.
    /// <para>v1.64.16 made <c>FormatHelper</c> invariant. It did not touch the interpolation holes,
    /// which still went through <c>CurrentCulture</c> — so the fix left a MIXED screen rather than a
    /// consistent one. Measured on ro-RO for the same 1.5 GB value:</para>
    /// <code>
    ///   FormatHelper.FormatSize(...)  ->  "1.5 GB"     (invariant, since v1.64.16)
    ///   $"{gb:F1} GB"                 ->  "1,5 GB"     (CurrentCulture)
    /// </code>
    /// <para>Two different decimal marks, side by side, in the same sentence — which is the exact
    /// inconsistency v1.64.16 set out to remove. The same applies to <c>N0</c> grouping: 1610 renders
    /// as "1,610" invariant, "1.610" on ro-RO/de-DE, and "1 610" on fr-FR/fi-FI.</para>
    /// <para>Only culture-SENSITIVE specifiers are checked. <c>F0</c> is deliberately excluded: a whole
    /// number with no grouping renders identically on every culture, so requiring a wrapper there would
    /// be churn with no behaviour change. Hex (<c>X8</c>) consults no culture data either.</para>
    /// <para>The fix is <c>string.Create(CultureInfo.InvariantCulture, $"…")</c>, which wraps the WHOLE
    /// string. That matters: a per-hole fix on a line with three holes can leave two of them behind,
    /// which is precisely how the first pass of the date migration missed eight sites.</para>
    /// </summary>
    [Fact]
    public void EveryCultureSensitiveNumberFormat_NamesItsCulture()
    {
        var appDir = FindAppProjectDir();
        var offenders = new List<string>();
        var wrapped = 0;

        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                              StringComparison.Ordinal)) continue;

            var source = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (var match in InterpolatedString().Matches(source).Cast<Match>())
            {
                if (!CultureSensitiveNumberHole().IsMatch(match.Value)) continue;

                var before = source[Math.Max(0, match.Index - 120)..match.Index];

                // A Serilog message template is not an interpolated string: Serilog parses it and
                // LogService builds the formatter with InvariantCulture, so the hole never reaches
                // string.Format. Rewriting one would also break structured logging.
                if (SerilogCall().IsMatch(before)) continue;

                if (InvariantWrapper().IsMatch(before)) { wrapped++; continue; }

                var line = source[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{name}:{line} {match.Value[..Math.Min(70, match.Value.Length)]}");
            }
        }

        // Vacuity floor: if the interpolated-string pattern stopped matching, this would pass while
        // inspecting nothing at all.
        Assert.True(wrapped >= 30,
            $"Only {wrapped} invariant-wrapped numeric strings were found — the detection is broken, "
            + "not the code. Fix this guard rather than trusting it.");

        Assert.True(offenders.Count == 0,
            "These numbers are formatted through CurrentCulture while FormatHelper is invariant, so on "
            + "a comma locale the same screen shows both \"1.5 GB\" and \"1,5 GB\". Wrap the whole string "
            + "in string.Create(CultureInfo.InvariantCulture, $\"…\"):\n  "
            + string.Join("\n  ", offenders)
            + $"\n({wrapped} already wrapped)");
    }

    // An interpolated string literal. The nested-quote alternative is load-bearing: C# allows
    // $"…{(b ? "y" : "n")}…", and a pattern without it silently skips those strings — one such site
    // (ShortcutCleanerViewModel) was missed by exactly that omission.
    [GeneratedRegex(@"\$""(?:[^""\\\n]|\\.|""(?=[^""\n]*""))*""", RegexOptions.CultureInvariant)]
    private static partial Regex InterpolatedString();

    // A decimal mark (F1+, N1+, P, 0.0) or a group separator (N0, #,#). F0 is absent on purpose.
    [GeneratedRegex(@"\{[^{}]{1,120}?:(?:F[1-9]|N[1-9]|N0|P\d+|0\.0+|#,#)", RegexOptions.CultureInvariant)]
    private static partial Regex CultureSensitiveNumberHole();

    [GeneratedRegex(@"(?:string\.Create\(\s*(?:System\.Globalization\.)?CultureInfo\.InvariantCulture\s*,\s*"
                    + @"|FormattableString\.Invariant\(\s*)$", RegexOptions.CultureInvariant)]
    private static partial Regex InvariantWrapper();

    [GeneratedRegex(@"\bLog(?:ger)?\s*\.\s*(?:Verbose|Debug|Information|Warning|Error|Fatal)\s*\([^)]*$",
                    RegexOptions.CultureInvariant)]
    private static partial Regex SerilogCall();

    /// <summary>
    /// Every icon glyph names an icon font, so none of them renders as a colour emoji.
    /// <para>A character reference above U+FFFF is outside Segoe Fluent Icons, so a <c>TextBlock</c>
    /// carrying one with no <c>FontFamily</c> falls back to Segoe UI Emoji and Windows draws a
    /// multi-colour emoji. The elevated admin banner did exactly that in 27 views:
    /// <c>&amp;#x1F6E1;</c> (SHIELD) with no font — while the NOT-elevated banner a few lines above it,
    /// in 25 of those same views, used <c>&amp;#xE83D;</c> with an explicit Fluent family. The two states
    /// of one banner drew their icon from two different type systems, and the emoji one contradicts the
    /// product rule that icons are real, never cartoonish.</para>
    /// <para>Checked as a character range rather than a list of known emoji, so a NEW emoji is caught
    /// too. A glyph that legitimately wants a colour emoji can still have one — it just has to say so
    /// by naming a font.</para>
    /// </summary>
    [Fact]
    public void EveryIconGlyph_NamesAnIconFont()
    {
        var viewsDir = Path.Combine(FindAppProjectDir(), "Views");
        var offenders = new List<string>();
        var scanned = 0;

        var files = Directory.GetFiles(viewsDir, "*.xaml")
            .Append(Path.Combine(FindAppProjectDir(), "MainWindow.xaml"))
            .Where(File.Exists);

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (var match in AstralCharacterReference().Matches(source).Cast<Match>())
            {
                scanned++;

                // The element this glyph sits on: from the opening '<' before it to the next '>'.
                var open = source.LastIndexOf('<', match.Index);
                var close = source.IndexOf('>', match.Index);
                if (open < 0 || close < 0) continue;
                var element = source[open..close];

                if (element.Contains("FontFamily", StringComparison.Ordinal)) continue;

                var line = source[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{name}:{line} {match.Value} has no FontFamily");
            }
        }

        // Vacuity floor: the views carry hundreds of Fluent glyphs, so if the scan read nothing the
        // character-reference pattern is broken rather than the code being clean.
        var glyphs = files.Sum(f => FluentGlyphReference().Matches(File.ReadAllText(f)).Count);
        Assert.True(glyphs >= 100,
            $"Only {glyphs} icon glyphs were seen across the views — the guard is not reading them. "
            + "Fix this test rather than trusting its pass.");

        Assert.True(offenders.Count == 0,
            "These glyphs are above U+FFFF and name no font, so Windows falls back to Segoe UI Emoji "
            + "and draws a colour emoji instead of an icon. Use the Segoe Fluent Icons equivalent with "
            + "FontFamily=\"Segoe Fluent Icons,Segoe MDL2 Assets\" (the shield is &#xE83D;):\n  "
            + string.Join("\n  ", offenders)
            + $"\n({scanned} astral references seen, {glyphs} icon glyphs scanned)");
    }

    // A character reference above U+FFFF — i.e. &#x1F???; and up, which is where the emoji planes are.
    // Five hex digits or more cannot be a BMP icon-font glyph.
    [GeneratedRegex(@"&#x[0-9A-Fa-f]{5,};", RegexOptions.CultureInvariant)]
    private static partial Regex AstralCharacterReference();

    [GeneratedRegex(@"&#x[0-9A-Fa-f]{4};", RegexOptions.CultureInvariant)]
    private static partial Regex FluentGlyphReference();

    /// <summary>
    /// The release workflow must RUN the exe it is about to publish, and must do so while a failure
    /// can still stop the release.
    /// <para>Nothing in the pipeline ever started the artifact. The unit tests exercise the code, but
    /// they load assemblies into a test host — they do not launch the single-file, self-contained,
    /// compressed exe a user downloads, and those are different failure surfaces: a native library that
    /// does not extract from the bundle, a startup path that throws before the first frame, a resource
    /// that resolves in a normal build but not a packed one. All of them pass every test and then fail
    /// on launch, which is exactly what shipped once.</para>
    /// <para>Position is the whole point, so it is asserted rather than trusted to review. A smoke check
    /// placed after "Create GitHub Release" still runs and still goes red — but the release, the winget
    /// submission and the announcement are already out, so it reports a fact instead of preventing one.
    /// It must sit after the artifact exists (the publish) and before anything is published.</para>
    /// </summary>
    [Fact]
    public void TheReleaseWorkflow_LaunchesTheExeBeforeItPublishesAnything()
    {
        var lines = File.ReadAllLines(
            Path.Combine(FindRepoRoot(), ".github", "workflows", "release.yml"));

        int StepLine(string name)
        {
            var at = Array.FindIndex(lines, l => l.Trim() == $"- name: {name}");
            Assert.True(at >= 0,
                $"release.yml has no step named \"{name}\". If it was renamed, update this guard in the "
                + "same PR — do not delete it.");
            return at;
        }

        var publish = StepLine("Publish single-file exe");
        var rename = StepLine("Rename exe with version");
        var smoke = StepLine("Smoke-check the published exe");
        var release = StepLine("Create GitHub Release");
        var winget = StepLine("Sync the winget-pkgs fork with upstream");
        var announce = StepLine("Post announcement to Discussions");

        Assert.True(smoke > rename,
            $"the smoke check (line {smoke + 1}) runs before the exe is named (line {rename + 1}), so "
            + "there is no artifact for it to launch.");
        Assert.True(publish < smoke, "the exe must be published to disk before it can be launched.");

        foreach (var (step, at) in new[]
                 {
                     ("Create GitHub Release", release),
                     ("Sync the winget-pkgs fork with upstream", winget),
                     ("Post announcement to Discussions", announce)
                 })
        {
            Assert.True(smoke < at,
                $"the smoke check (line {smoke + 1}) runs AFTER \"{step}\" (line {at + 1}). A launch "
                + "failure would then be reported rather than prevented — the release, the package "
                + "submission and the announcement would already be public.");
        }

        // The step body has to actually start the process and judge the outcome. Without these it
        // could be reduced to an echo and still satisfy the ordering above.
        var body = string.Join('\n', lines[smoke..release]);
        foreach (var required in new[] { "Start-Process", "HasExited", "last-crash.json", "throw" })
        {
            Assert.Contains(required, body, StringComparison.Ordinal);
        }

        // A check that never fails the job is decoration. continue-on-error on this step would make
        // the launch advisory, which is the one thing it must not be.
        Assert.DoesNotContain("continue-on-error", body, StringComparison.Ordinal);

        // It must also NOT close the window politely. A CI runner is always a FIRST launch, so
        // close-preference.json does not exist, ClosePreferenceService.Load() returns Ask, and
        // MainWindow.OnClosing raises a modal MessageBox asking whether to keep running in the
        // notification area. Nothing answers it, so the polite close times out — the step warned
        // "the window did not close within 15s" on the very first release that ran it (v1.65.6).
        // The prompt is correct behaviour (it is the fix for #1639/#1827); the polite close was the
        // wrong check, and a warning that fires on every release is how a gate gets ignored.
        //
        // Matched as a CALL — `.CloseMainWindow(` — not as the bare word, because the step's own
        // comment has to be able to name the thing it deliberately does not do. Asserting on the word
        // made this fail against the fixed tree, which is a guard that forbids its own explanation.
        Assert.DoesNotContain(".CloseMainWindow(", body, StringComparison.Ordinal);
        Assert.Contains(".Kill()", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// No style may suppress the keyboard focus indicator without providing a replacement.
    /// <para><c>FocusVisualStyle="{x:Null}"</c> removes the only cue a keyboard user has, and four
    /// styles did exactly that with nothing in its place: ButtonBase, ToggleSwitch, DataGridCell and
    /// ConsoleView's log rows. ButtonBase's template substituted an Accent-coloured border, which
    /// cannot work for the styles derived from it — PrimaryButton's fill IS the accent (1.00:1,
    /// invisible on all 12 presets) and DangerButton's is red (1.02–1.75:1), both far below WCAG
    /// 1.4.11's 3:1 for a non-text indicator. Seven templated interactive styles never had a ring at
    /// all.</para>
    /// <para>The fix is one shared <c>FocusRing</c> adorner, so this asserts the SHAPE of the fix
    /// rather than the count: nulling the focus visual is allowed nowhere, and every style that
    /// replaces the default template of a focusable control must name the shared ring. That way the
    /// next templated control is caught at build time instead of by a keyboard user.</para>
    /// </summary>
    [Fact]
    public void NoStyle_SuppressesTheKeyboardFocusIndicator()
    {
        var appDir = FindAppProjectDir();
        var files = Directory
            .EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(appDir, f)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.True(files.Length >= 60, $"only {files.Length} XAML files were found — fix this guard.");

        var nulled = new List<string>();
        var ringUses = 0;

        foreach (var path in files)
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (NulledFocusVisual().IsMatch(lines[i]))
                    nulled.Add($"{Path.GetFileName(path)}:{i + 1}");
                if (lines[i].Contains("FocusVisualStyle", StringComparison.Ordinal)
                    && lines[i].Contains("FocusRing", StringComparison.Ordinal))
                {
                    ringUses++;
                }
            }
        }

        Assert.True(nulled.Count == 0,
            "these styles remove the keyboard focus indicator and put nothing back, so a keyboard user "
            + "cannot see what is focused (WCAG 2.4.7). Point FocusVisualStyle at the shared FocusRing "
            + $"instead of {{x:Null}}:\n  {string.Join("\n  ", nulled)}");

        // Vacuity floor: the ring must actually be referenced. Deleting every reference would satisfy
        // the assert above while leaving the app with no focus cue at all.
        Assert.True(ringUses >= 6,
            $"only {ringUses} styles reference the shared FocusRing — a control whose template replaces "
            + "the default one loses the focus adorner, so it has to name the ring explicitly.");

        // And the ring itself must be two strokes of opposite tone. A single-colour ring is what failed
        // on the accent and red fills; reducing it back to one would restore the defect while keeping
        // every assertion above green.
        var app = File.ReadAllText(Path.Combine(appDir, "App.xaml"));
        var start = app.IndexOf("<Style x:Key=\"FocusRing\">", StringComparison.Ordinal);
        Assert.True(start >= 0, "App.xaml no longer defines FocusRing — update this guard, don't drop it.");
        var end = app.IndexOf("</Style>", start, StringComparison.Ordinal);
        Assert.True(end > start, "the FocusRing style is not terminated; the slice below would be empty.");

        var ring = app[start..end];
        Assert.Equal(2, StrokeAttribute().Matches(ring).Count);
        Assert.Contains("Stroke=\"#111111\"", ring, StringComparison.Ordinal);
        Assert.Contains("Stroke=\"#FFFFFF\"", ring, StringComparison.Ordinal);
        // Accent must NOT be the ring colour: that is the whole defect, and it is also the hover and
        // selection colour, so a well-meaning "use the theme brush" edit would reintroduce 1.00:1.
        Assert.DoesNotContain("Accent", ring, StringComparison.Ordinal);
    }

    /// <summary>A style that suppresses the focus indicator outright.</summary>
    [GeneratedRegex(@"FocusVisualStyle""\s*Value=""\{x:Null\}""", RegexOptions.Compiled)]
    private static partial Regex NulledFocusVisual();

    /// <summary>A literal Stroke colour on the focus ring's rectangles.</summary>
    [GeneratedRegex(@"Stroke=""#[0-9A-Fa-f]{6}""", RegexOptions.Compiled)]
    private static partial Regex StrokeAttribute();

    /// <summary>
    /// No log statement sits unguarded inside a hardware-enumeration loop that a poll re-enters.
    /// <para><c>TemperatureService.ReadViaLibreHardwareMonitor</c> logged one line per hardware item
    /// per call, and the Dashboard polls it every 2 seconds
    /// (<c>DashboardViewModel.StartTemperaturePolling</c>, <c>Task.Delay(2000)</c>) — so a machine with
    /// four LHM devices produced four Debug lines every two seconds for as long as the app ran. The
    /// v1.65.6 release smoke-check dumps the last 40 log lines when a launch fails; all 40 were that
    /// one message, which means a real fault would have been pushed out of the window by noise. The log
    /// is also the only diagnostic a user can send, and it is bounded (10 MB × 14 files), so the spam
    /// evicts genuine history.</para>
    /// <para>Sensor topology is static hardware identity — the same class already memoizes disk names
    /// and NvAPI init for exactly that reason — so it is logged once per session behind a flag. This
    /// asserts the flag exists, guards the log, and is only set after the loop completes, because
    /// setting it before would lose the remaining hardware if a read threw partway through.</para>
    /// <para>Cannot be a behavioural test: the LHM path needs administrator rights and real sensors,
    /// so <c>ReadAllAsync</c> returns early under <c>skipHardwareInit</c> in every test. The shape of
    /// the fix is assertable from source; the behaviour is not.</para>
    /// </summary>
    [Fact]
    public void TheSensorTopologyLog_RunsOncePerSession_NotOncePerPoll()
    {
        var source = File.ReadAllText(
            Path.Combine(FindAppProjectDir(), "Services", "TemperatureService.cs"));

        const string flag = "_loggedSensorTopology";
        Assert.Contains($"private bool {flag};", source, StringComparison.Ordinal);

        // The poll loop is the thing that makes an unguarded log expensive, so pin that it is still a
        // loop: if the enumeration were ever restructured, this guard should be revisited, not passed.
        var loopAt = source.IndexOf("foreach (var hardware in _computer.Hardware)", StringComparison.Ordinal);
        Assert.True(loopAt > 0, "the LHM hardware loop was not found — fix this guard, do not delete it.");

        var logAt = source.IndexOf("LHM: {Type}", StringComparison.Ordinal);
        Assert.True(logAt > loopAt, "the topology log is no longer inside the hardware loop.");

        // The log must sit behind the flag. Checked as the text between the loop head and the log call,
        // so a guard placed anywhere else in the file cannot satisfy this.
        var beforeLog = source[loopAt..logAt];
        Assert.Contains($"if (!{flag})", beforeLog, StringComparison.Ordinal);

        // And the flag must be set AFTER the loop body, not before or inside it: setting it on the
        // first hardware item would drop every later device from the one session that logs them.
        var setAt = source.IndexOf($"{flag} = true;", StringComparison.Ordinal);
        Assert.True(setAt > logAt,
            $"{flag} is set at {setAt} but the log is at {logAt} — it must be set after the loop, so a "
            + "read that throws partway through can still log the rest on its next attempt.");
    }

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
