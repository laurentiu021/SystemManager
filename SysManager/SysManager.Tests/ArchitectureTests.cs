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
    /// The README's table of contents must resolve. It is 1300+ lines long, so the contents list is the
    /// only practical way to navigate it — and a heading rename silently breaks an anchor, which renders
    /// as a link that quietly does nothing rather than as an error.
    /// </summary>
    [Fact]
    public void TheReadmeTableOfContents_HasNoDeadAnchors()
    {
        var lines = File.ReadAllLines(Path.Combine(FindRepoRoot(), "README.md"));

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

        Assert.True(entries >= 10, $"expected the full contents list, found {entries} entries");
        Assert.True(dead.Count == 0,
            "these table-of-contents links point at headings that do not exist:\n  " + string.Join("\n  ", dead));
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
