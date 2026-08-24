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
        // The ONE that remains is the hard one:
        //   · LogService is a `static partial class` by design — Serilog's sink is configured once per
        //     process — so it has no instance to hang the shared `string? configDir = null` seam on. It
        //     is also the only offender no test constructs, so its risk is the lowest of the set; it
        //     needs a design decision (dropping the static-sink model) rather than a mechanical edit.
        //
        // ThemeService came off in #1741's follow-up: its path moved to an instance field set from the
        // same seam, and its one WPF touch-point (Apply) now no-ops without an Application, so the
        // persistence path is exercised headlessly by ThemeServiceTests instead of being left untested.
        string[] known =
        [
            "LogService.<LogDir>k__BackingField",
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

                // ORDER, not co-presence — the name says "IsCancelledFirst" and the failure message says
                // "before the Dispose()". Co-presence accepted `_cts.Dispose(); _cts.Cancel();`, where the
                // Cancel throws ObjectDisposedException and in-flight work (shredder overwrites, cleanup
                // deletes) survives teardown: the exact defect, with both tokens present.
                var cancelAt = FirstIndexOfAny(body, $"{field}?.Cancel()", $"{field}.Cancel()");
                var disposeAt = FirstIndexOfAny(body, $"{field}?.Dispose()", $"{field}.Dispose()");
                if (cancelAt < 0)
                    offenders.Add($"{Path.GetFileName(file)} · {field} — never cancelled");
                else if (cancelAt > disposeAt)
                    offenders.Add($"{Path.GetFileName(file)} · {field} — cancelled AFTER being disposed");
            }
        }

        // Vacuity floor: if the field-detection regexes stopped matching, every assertion here would
        // pass while inspecting nothing.
        Assert.True(checkedFields >= 25,
            $"Expected at least 25 disposed cancellation sources, found {checkedFields} — " +
            "the detection is probably no longer matching the field declarations.");

        Assert.True(offenders.Count == 0,
            "These cancellation sources are disposed without being cancelled first, so work already in "
            + "flight keeps running after teardown — Dispose() does not cancel. Add a Cancel() before "
            + "the Dispose():\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Index of whichever needle appears first, or -1 when neither does. Used to compare the POSITION of
    /// two calls rather than merely their presence.
    /// </summary>
    private static int FirstIndexOfAny(string haystack, params string[] needles)
    {
        var best = -1;
        foreach (var needle in needles)
        {
            var at = haystack.IndexOf(needle, StringComparison.Ordinal);
            if (at >= 0 && (best < 0 || at < best)) best = at;
        }
        return best;
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

    /// <summary>
    /// No public document may claim the update path enforces a publisher while
    /// <c>UpdateService.ExpectedSignerSubject</c> is still empty.
    /// <para>The README said, in the present tense, that a signed update "must belong to the expected
    /// publisher and its certificate chain must validate, so a build signed by someone else is
    /// refused". The pin is empty until a certificate exists, so <c>VerifyAuthenticode</c> returns true
    /// for ANY signed binary and neither the subject comparison nor the chain build is reached — the
    /// document described the code that will run one day, not the code that ships. A reader deciding
    /// whether to trust an auto-update was being told a check protects them that does not run yet.</para>
    /// <para>The claim is not wrong forever, which is exactly why a human reviewer misses it: it becomes
    /// TRUE the day the constant is filled in. So this guard is conditional rather than a blocklist —
    /// while the pin is empty the assertive phrasings are forbidden, and once it is set they are
    /// required, which also catches the opposite drift of shipping signing while the docs still say
    /// "unsigned". SECURITY.md's wording was already honest ("empty until a code-signing certificate
    /// exists") and passes unchanged; that asymmetry is what proved the README was the drift.</para>
    /// </summary>
    [Fact]
    public void NoPublicDocument_ClaimsAPublisherPinThatIsNotArmed()
    {
        var root = FindRepoRoot();
        var pinIsArmed = SysManager.Services.UpdateService.ExpectedSignerSubject.Length > 0;

        string[] surfaces = ["README.md", "SECURITY.md"];
        var offenders = new List<string>();
        var honestDisclosures = 0;

        foreach (var relative in surfaces)
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), $"{relative} not found — the guard would pass vacuously");

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                // Counts the phrasing that discloses the pin is not armed yet, in either document.
                if (UnarmedPinDisclosure().IsMatch(lines[i])) honestDisclosures++;

                if (!pinIsArmed && AssertsPublisherIsEnforced().IsMatch(lines[i]))
                    offenders.Add($"{relative}:{i + 1}  {lines[i].Trim()}");
            }
        }

        if (pinIsArmed)
        {
            // Signing went live: the docs must now SAY so. Flip side of the same contract.
            Assert.True(honestDisclosures == 0,
                "ExpectedSignerSubject is now set, so the publisher check really does run — but a public "
                + "document still says no publisher is pinned. Update the docs to match the code.");
            return;
        }

        // Vacuity floor: both documents must disclose the not-yet-armed state, or this guard is
        // measuring a corpus that no longer discusses the pin at all.
        Assert.True(honestDisclosures >= 2,
            "expected the docs to disclose that the publisher pin is not armed yet; found "
            + $"{honestDisclosures} such statements. Either the wording changed shape or the guard has "
            + "gone vacuous — fix the guard rather than trusting its pass.");

        Assert.True(offenders.Count == 0,
            "these lines claim the update path enforces a publisher or refuses a foreign signature, but "
            + "UpdateService.ExpectedSignerSubject is empty, so VerifyAuthenticode accepts any signed "
            + "binary and the pin and chain checks are unreachable. State that the check is written but "
            + $"not yet armed:\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>
    /// Prose asserting the publisher check is enforced today: "must belong to the expected publisher",
    /// "signed by someone else is refused", "checks that it belongs to the expected publisher".
    /// Deliberately matches the ASSERTION, not the words "expected publisher" alone, so a sentence that
    /// explains the pin is empty does not trip it.
    /// </summary>
    [GeneratedRegex(@"(?:must (?:belong to|match) the (?:expected|pinned) publisher"
        + @"|checks that it belongs to the expected publisher"
        + @"|signed by (?:someone|anyone) else (?:is|will be) refused)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AssertsPublisherIsEnforced();

    /// <summary>Prose disclosing that the pin is not armed yet — the honest counterpart.</summary>
    [GeneratedRegex(@"(?:no publisher is pinned yet"
        + @"|empty until a (?:code-signing )?certificate exists"
        + @"|not yet armed)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UnarmedPinDisclosure();

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

        // Cross-PAGE anchors — [text](SECURITY.md#security-model) — break the same silent way, and were
        // invisible to the sweep above: InPageLink only matches "](#anchor)", never "](other.md#anchor)".
        // Six such links already existed when this was added, all resolving by luck rather than by check.
        var crossPage = 0;
        var headingsByPage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly))
        {
            var page = File.ReadAllLines(path);
            var set = page
                .Select(l => HeadingLine().Match(l))
                .Where(m => m.Success)
                .Select(m => Slug(m.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var l in page.Where(l => l.StartsWith("# ", StringComparison.Ordinal)))
                set.Add(Slug(l[2..]));
            headingsByPage[Path.GetFileName(path)] = set;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly))
        {
            var page = File.ReadAllLines(path);
            for (var i = 0; i < page.Length; i++)
            {
                foreach (var hit in CrossPageLink().Matches(page[i]).Cast<Match>())
                {
                    crossPage++;
                    var (target, anchor) = (hit.Groups["file"].Value, hit.Groups["anchor"].Value);
                    if (!headingsByPage.TryGetValue(target, out var targetHeadings))
                        dead.Add($"{Path.GetFileName(path)}:{i + 1}  {target}#{anchor}  (no such page)");
                    else if (!targetHeadings.Contains(anchor))
                        dead.Add($"{Path.GetFileName(path)}:{i + 1}  {target}#{anchor}");
                }
            }
        }

        Assert.True(pages >= 6, $"expected the top-level doc pages, found {pages}");
        Assert.True(entries >= 10, $"expected the full contents list, found {entries} entries");
        Assert.True(crossPage >= 4,
            $"expected the doc set's cross-page anchor links, found {crossPage} — either they were "
            + "removed or the pattern no longer matches them, and this half of the guard is vacuous");
        Assert.True(dead.Count == 0,
            "these links point at headings that do not exist, so they render as text that "
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

    /// <summary>
    /// A link into ANOTHER top-level doc page's heading, capturing the file and the anchor —
    /// <c>](SECURITY.md#security-model)</c>. Relative paths with a directory are excluded: this guard
    /// only knows the headings of the top-level pages it enumerated.
    /// </summary>
    [GeneratedRegex(@"\]\((?<file>[A-Za-z0-9_.-]+\.md)#(?<anchor>[^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex CrossPageLink();

    /// <summary>
    /// An XML/XAML comment block. Stripped before asserting that a view BINDS something: a comment that
    /// merely names a property would otherwise satisfy a substring check on the raw file.
    /// </summary>
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex XmlComment();

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
        //
        // Sliced to the END OF THIS STEP, not to "Create GitHub Release": two unrelated steps sit in
        // between ("Extract release notes from CHANGELOG" and "Append verification instructions"), and
        // between them they contain five `throw`s. Against the wider slice, replacing this step's own
        // `throw` with a Write-Warning — turning the launch gate into exactly the "decoration" the
        // paragraph below forbids — still left the assertion green.
        var stepEnd = Array.FindIndex(lines, smoke + 1, l => l.TrimStart().StartsWith("- name:", StringComparison.Ordinal));
        Assert.True(stepEnd > smoke && stepEnd <= release,
            $"the smoke-check step has no following step before \"Create GitHub Release\" (found {stepEnd}). "
          + "The slice must end at this step's own boundary: widening it to the next few steps lets THEIR "
          + "five throws stand in for this gate's, which is how a neutered launch check stayed green.");
        var body = string.Join('\n', lines[smoke..stepEnd]);
        Assert.True(body.Length > 500,
            $"the smoke-check step body is only {body.Length} characters — the slice has collapsed, so "
          + "the token checks below would pass by measuring nothing.");
        foreach (var foreign in new[] { "Extract release notes from CHANGELOG", "Create GitHub Release" })
        {
            Assert.DoesNotContain(foreign, body, StringComparison.Ordinal);
        }
        foreach (var required in new[] { "Start-Process", "HasExited", "last-crash.json" })
        {
            Assert.Contains(required, body, StringComparison.Ordinal);
        }

        // `throw` is counted on non-comment lines only, and more than one is required: the step raises on
        // three distinct verdicts (the exe self-exited, it left a crash marker, it would not die when
        // killed). A bare Contains was satisfied by the word "throw" in this step's own explanatory
        // comment, and by whichever verdict was left intact when another was turned into a Write-Warning.
        var throwingLines = body.Split('\n')
            .Count(l => !l.TrimStart().StartsWith('#') && l.Contains("throw ", StringComparison.Ordinal));
        Assert.True(throwingLines >= 3,
            $"the smoke check raises on only {throwingLines} verdict(s). It must fail the job when the exe "
          + "self-exits, when it leaves a crash marker, AND when it will not terminate — a verdict "
          + "downgraded to a warning makes the launch advisory, which is the one thing it must not be.");

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
    /// The release workflow must prove the artifact reports the tag it was built from — statically,
    /// and again at runtime.
    /// <para>publish.ps1 injects Version, FileVersion and AssemblyVersion from the tag and nothing
    /// downstream ever read them back. ci.yml checks that the three csproj values agree with each
    /// OTHER, which says nothing about the binary. A silently broken injection ships a build that
    /// misreports itself in About, in the bug-report URL, in the profile export and in the system
    /// report — and in the update check, where AboutViewModel compares UpdateService.CurrentVersion
    /// against the newest release, so a stale stamp offers every user the same update forever or
    /// hides a real one.</para>
    /// <para>Two assertions, because two different values are at risk. The Win32 version resource
    /// (FileVersion/ProductVersion) is readable without running anything, so it is checked before the
    /// SBOM and the attestation: attesting a mis-stamped binary is a signed public claim that cannot
    /// be taken back. The managed assembly version — the one every user-visible version actually
    /// reads — is NOT in that resource, and AssemblyName.GetAssemblyName throws on a single-file
    /// apphost, so the only place it is observable is the startup line in the log of the launch the
    /// smoke check already performs.</para>
    /// <para>The expected log shape is DERIVED from <see cref="LogService.StartupMessage"/> rather
    /// than written out again here, so the workflow's pattern and the app's message cannot drift into
    /// a gate that greps for a line the app no longer writes.</para>
    /// </summary>
    [Fact]
    public void TheReleaseWorkflow_ProvesThePublishedBinaryReportsTheTag()
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

        // Sliced to the step's OWN boundary and required to be substantial: a collapsed slice would
        // make every token check below pass by measuring nothing.
        string CodeOf(string step, int at)
        {
            var end = Array.FindIndex(lines, at + 1,
                l => l.TrimStart().StartsWith("- name:", StringComparison.Ordinal));
            Assert.True(end > at,
                $"\"{step}\" is the last step in the file, so its slice is unbounded and this guard "
                + "would measure the rest of the workflow instead of the step.");
            // Comment lines are dropped before any assertion. This step's own explanation names
            // FileVersion, ProductVersion and the assembly version, so a Contains against the raw
            // slice would be satisfied by the prose describing the check rather than the check.
            var code = string.Join('\n', lines[at..end]
                .Where(l => !l.TrimStart().StartsWith('#')));
            Assert.True(code.Length > 300,
                $"the code in \"{step}\" is only {code.Length} characters once comments are removed — "
                + "the check has been reduced to its own description.");
            return code;
        }

        var rename = StepLine("Rename exe with version");
        var stamp = StepLine("Verify the embedded version stamp");
        var sbom = StepLine("Generate CycloneDX SBOM");
        var attest = StepLine("Attest build provenance");
        var smoke = StepLine("Smoke-check the published exe");
        var release = StepLine("Create GitHub Release");

        Assert.True(stamp > rename,
            $"the stamp check (line {stamp + 1}) runs before the exe is named (line {rename + 1}), so "
            + "the file it resolves does not exist yet.");
        foreach (var (step, at) in new[] { ("Generate CycloneDX SBOM", sbom),
                                           ("Attest build provenance", attest),
                                           ("Create GitHub Release", release) })
        {
            Assert.True(stamp < at,
                $"the stamp check (line {stamp + 1}) runs AFTER \"{step}\" (line {at + 1}). The "
                + "attestation is a signed public claim about a specific binary — it must never be "
                + "made about one whose version was not verified first.");
        }

        var stampCode = CodeOf("Verify the embedded version stamp", stamp);
        foreach (var required in new[] { "VersionInfo", "FileVersion", "ProductVersion" })
        {
            Assert.Contains(required, stampCode, StringComparison.Ordinal);
        }

        // Both halves must be able to FAIL the job. One throw would leave whichever value lost its
        // verdict silently unverified, which is the state this whole guard exists to end.
        var stampThrows = stampCode.Split('\n').Count(l => l.Contains("throw ", StringComparison.Ordinal));
        Assert.True(stampThrows >= 2,
            $"the stamp check raises on only {stampThrows} verdict(s); it must fail the job for a wrong "
            + "FileVersion AND for a wrong ProductVersion.");
        Assert.DoesNotContain("continue-on-error", stampCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Warning", stampCode, StringComparison.Ordinal);

        // The runtime half lives inside the smoke check because that is where the app is running.
        var smokeCode = CodeOf("Smoke-check the published exe", smoke);
        Assert.Contains(
            LogService.StartupMessage.Replace("{Version}", ".*", StringComparison.Ordinal),
            smokeCode, StringComparison.Ordinal);
        Assert.Contains(
            LogService.StartupMessage.Replace("{Version}", "$env:VERSION", StringComparison.Ordinal),
            smokeCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Warning", smokeCode, StringComparison.Ordinal);

        // A missing log or a missing startup line must be a failure, not a silent pass — an absent
        // line is indistinguishable from a matching one to any check that only compares when it
        // finds something.
        var startupChecks = smokeCode.Split('\n')
            .Count(l => l.Contains("throw ", StringComparison.Ordinal));
        Assert.True(startupChecks >= 6,
            $"the smoke check raises on only {startupChecks} verdict(s). Three belong to the launch "
            + "(self-exit, crash marker, will not die) and three to the version (no log, no startup "
            + "line, wrong version) — an unverifiable version must fail rather than pass quietly.");

        // Finally, the app side of the contract: the gate can only read a version out of the log
        // while Init still puts one there.
        Assert.StartsWith("SysManager ", LogService.StartupMessage, StringComparison.Ordinal);
        Assert.Contains("{Version}", LogService.StartupMessage, StringComparison.Ordinal);

        var initCall = File.ReadAllLines(
                Path.Combine(FindRepoRoot(), "SysManager", "SysManager", "Services", "LogService.cs"))
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .FirstOrDefault(l => l.Contains("Information(StartupMessage", StringComparison.Ordinal));
        Assert.False(initCall is null,
            "LogService no longer logs StartupMessage, so the release gate reads a line nobody writes.");
        Assert.Contains("UpdateService.CurrentVersion", initCall!, StringComparison.Ordinal);
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

    /// <summary>
    /// Every control inside a DataGrid row must announce WHICH ROW it belongs to, and the name must be
    /// somewhere an automation peer can actually read it.
    /// <para>Two distinct defects, both of which look correct in the markup. App Blocker set
    /// <c>AutomationProperties.Name="Select application"</c> on the <c>DataGridCheckBoxColumn</c> itself —
    /// but a column is a definition, not a visual, so it has no automation peer and the generated
    /// CheckBox in every cell stayed unlabelled. Startup Manager's per-row "Open" button had no name at
    /// all, so all of its rows announced the single word "Open" with nothing to say which program would
    /// be opened. A row control that announces the same thing on every row is barely better than one
    /// that announces nothing: the user can hear it but cannot tell the rows apart.</para>
    /// <para>Both populations are derived from the XAML tree rather than from a known list, so the next
    /// column or the next row button is caught without editing this test. Attribute lookup is by local
    /// name: <c>AutomationProperties.Name</c> is written unprefixed in XAML, so it arrives as a single
    /// attribute whose name contains a dot.</para>
    /// </summary>
    [Fact]
    public void EveryRowControl_AnnouncesTheRowItIsOn()
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

        static bool HasName(System.Xml.Linq.XElement e) =>
            e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name");

        static bool IsInsideAColumn(System.Xml.Linq.XElement e) =>
            e.Ancestors().Any(a => a.Name.LocalName.EndsWith("Column", StringComparison.Ordinal));

        var namedOnTheColumn = new List<string>();
        var unnamedRowControls = new List<string>();
        var sameOnEveryRow = new List<string>();
        var columnsSeen = 0;
        var namedRowControls = 0;
        var rowNameSetters = 0;

        foreach (var path in files)
        {
            var root = System.Xml.Linq.XDocument.Load(path).Root;
            if (root is null) continue;
            var file = Path.GetFileName(path);

            foreach (var element in root.DescendantsAndSelf())
            {
                var name = element.Name.LocalName;

                // A column definition. Its own Name reaches nothing.
                if (name.EndsWith("Column", StringComparison.Ordinal)
                    && !name.EndsWith("ColumnDefinition", StringComparison.Ordinal))
                {
                    columnsSeen++;
                    if (HasName(element))
                        namedOnTheColumn.Add($"{file} — <{name}> carries the name itself");
                    continue;
                }

                // The ElementStyle form: a Setter reaching the control the column generates. This is
                // where a CheckBox column's name belongs, and six columns already do it this way.
                if (name == "Setter"
                    && (string?)element.Attribute("Property") == "AutomationProperties.Name"
                    && IsInsideAColumn(element))
                {
                    rowNameSetters++;
                    var value = (string?)element.Attribute("Value") ?? "";
                    if (!value.Contains("{Binding", StringComparison.Ordinal))
                        sameOnEveryRow.Add($"{file} — <Setter> value \"{value}\"");
                    continue;
                }

                // A pressable control living in a cell template.
                if (name is not ("Button" or "ToggleButton" or "RepeatButton")) continue;
                if (!IsInsideAColumn(element)) continue;

                var declared = element.Attributes()
                    .FirstOrDefault(a => a.Name.LocalName == "AutomationProperties.Name")?.Value;
                if (declared is null)
                {
                    var content = (string?)element.Attribute("Content")
                                  ?? (string?)element.Attribute("ToolTip")
                                  ?? "(no Content)";
                    unnamedRowControls.Add($"{file} — <{name}> \"{content}\"");
                }
                else
                {
                    namedRowControls++;
                    if (!declared.Contains("{Binding", StringComparison.Ordinal))
                        sameOnEveryRow.Add($"{file} — <{name}> name \"{declared}\"");
                }
            }
        }

        // Vacuity floors. Both checks are absence-based, so a selector that stopped matching would
        // report a clean sweep of nothing at all.
        Assert.True(columnsSeen >= 100,
            $"only {columnsSeen} DataGrid columns were found across {files.Length} views — the element "
            + "selector has stopped matching, so the column check below is measuring nothing.");
        Assert.True(namedRowControls >= 10,
            $"only {namedRowControls} named row controls were found — either the ancestor test or the "
            + "attribute lookup has stopped matching, and the sweep proves nothing.");
        Assert.True(rowNameSetters >= 10,
            $"only {rowNameSetters} ElementStyle name setters were found — six checkbox columns carry a "
            + "pair each, so a lower count means the Setter selector has stopped matching.");

        Assert.True(namedOnTheColumn.Count == 0,
            "AutomationProperties.Name is set on a DataGrid COLUMN, which is a definition rather than a "
            + "visual: it has no automation peer, so the control generated in each cell is announced "
            + "unlabelled. Move it into the column's ElementStyle (and EditingElementStyle for an "
            + "editable column) as a Setter, where the row is the DataContext and the name can name "
            + "it:\n  " + string.Join("\n  ", namedOnTheColumn));

        Assert.True(unnamedRowControls.Count == 0,
            "these controls sit in a DataGrid cell template with no accessible name, so every row "
            + "announces the same word — or nothing — and a screen-reader user cannot tell which row "
            + "the control belongs to. Bind the name to a property of the row, as the sibling columns "
            + $"do:\n  " + string.Join("\n  ", unnamedRowControls));

        // A constant name is the same defect one step later: present, readable, and identical on all
        // forty rows, so it still cannot tell them apart. Every one of the existing names binds a row
        // property, so this is the established shape rather than a new demand.
        Assert.True(sameOnEveryRow.Count == 0,
            "these row names are constants, so every row announces the same words and a screen-reader "
            + "user still cannot tell which row the control acts on. Bind a property of the row "
            + $"instead:\n  " + string.Join("\n  ", sameOnEveryRow));
    }

    /// <summary>
    /// No two progress bars on the same page may be announced by the same name.
    /// <para>Forty-four bars were announced as the bare word "Progress", and on four pages two or three
    /// appeared at once: Deep Cleanup showed separate scan, cleanup and large-file bars, all three saying
    /// "Progress". Worse, two were not progress at all — Disk Analyzer's drive-usage bar and Battery
    /// Health's charge bar are GAUGES, so a screen reader announced "Progress 78" for a battery that is
    /// 78% charged. Each now says what it reports.</para>
    /// <para>The rule is uniqueness per view rather than "never call a bar Progress": on the 34 pages
    /// with a single bar the bare word is uninformative but not ambiguous, and renaming those would mean
    /// inventing 34 strings for no behavioural gain. Ambiguity is the defect; vagueness is polish.</para>
    /// <para>Unnamed bars are ratcheted, not forbidden, and the allowance has since been tightened from
    /// ten to FOUR. The six that carried a <c>Value</c> binding — the Dashboard's CPU, memory and GPU
    /// gauges, its per-drive space bar, its quick-action bar, and Volume Control's per-session peak meter
    /// — report a real reading, so they were named (#1939); the two per-row ones name their row, as the
    /// guard above requires.</para>
    /// <para>What remains is the four DECORATIVE strips: <c>IsIndeterminate="True"</c> with no
    /// <c>Value</c> at all (Bulk Installer, MainWindow, two on the Dashboard). A spinner that only means
    /// "working" arguably belongs OUT of the accessibility tree rather than named, and no convention for
    /// hiding an element exists anywhere in this app yet — inventing one is a design decision, so it stays
    /// on #1939. The ratchet lets these counts fall but never rise, so the debt is recorded in code rather
    /// than in a comment nobody runs.</para>
    /// </summary>
    [Fact]
    public void NoTwoProgressBarsOnAPage_AreAnnouncedTheSame()
    {
        var appDir = FindAppProjectDir();
        var files = Directory
            .EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(appDir, f)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        // Views that still hold a bar with no name at all, with the count each is allowed. Fewer is
        // always fine; one more is a regression.
        // Only the decorative indeterminate strips are left. AudioMixerView is deliberately absent now:
        // its peak meter was named, so a new unnamed bar there must fail.
        var unnamedAllowance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["DashboardView.xaml"] = 2,
            ["BulkInstallerView.xaml"] = 1,
            ["MainWindow.xaml"] = 1,
        };

        var ambiguous = new List<string>();
        var unnamedOverAllowance = new List<string>();
        var sameOnEveryRow = new List<string>();
        var barsSeen = 0;
        var repeatingBars = 0;

        foreach (var path in files)
        {
            var root = System.Xml.Linq.XDocument.Load(path).Root;
            if (root is null) continue;
            var file = Path.GetFileName(path);

            var named = new List<string>();
            var unnamed = 0;

            foreach (var bar in root.DescendantsAndSelf()
                         .Where(e => e.Name.LocalName == "ProgressBar"))
            {
                barsSeen++;
                var name = bar.Attributes()
                    .FirstOrDefault(a => a.Name.LocalName == "AutomationProperties.Name")?.Value;
                if (string.IsNullOrWhiteSpace(name)) unnamed++;
                else named.Add(name.Trim());

                // A bar inside a DataTemplate is drawn once per item, so a CONSTANT name says the same
                // thing on every row and identifies none of them. The row guard above cannot see these:
                // it is scoped to the Button family inside a DataGrid column, and these sit in an
                // ItemsControl. A mutation that replaced one of these bound names with a constant went
                // green, which is how the gap was found.
                var repeats = bar.Ancestors().Any(a => a.Name.LocalName == "DataTemplate");
                if (!repeats) continue;
                repeatingBars++;
                if (!string.IsNullOrWhiteSpace(name)
                    && !name.Contains("{Binding", StringComparison.Ordinal))
                {
                    sameOnEveryRow.Add($"{file} — repeating bar announced \"{name.Trim()}\" on every row");
                }
            }

            foreach (var group in named.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
                ambiguous.Add($"{file} — {group.Count()} bars all announced \"{group.Key}\"");

            var allowed = unnamedAllowance.TryGetValue(file, out var cap) ? cap : 0;
            if (unnamed > allowed)
                unnamedOverAllowance.Add($"{file} — {unnamed} unnamed bar(s), {allowed} allowed");
        }

        // Vacuity floors: absence checks over populations the guard discovers itself.
        Assert.True(barsSeen >= 50,
            $"only {barsSeen} progress bars were found across {files.Length} XAML files — the element "
            + "selector has stopped matching, so this guard is measuring nothing.");
        Assert.True(repeatingBars >= 4,
            $"only {repeatingBars} progress bars were found inside a DataTemplate — the ancestor test has "
            + "stopped matching, so the per-row rule below is measuring nothing.");

        Assert.True(ambiguous.Count == 0,
            "these pages show more than one progress bar announced by the same name, so a screen-reader "
            + "or voice user cannot tell which one is being reported. Name each bar for what it "
            + $"reports:\n  " + string.Join("\n  ", ambiguous));

        Assert.True(unnamedOverAllowance.Count == 0,
            "a progress bar with no accessible name is announced with no identity at all. The remaining "
            + "ones are ratcheted (see this test's summary); this list means a NEW one appeared, or one "
            + "moved into a view that had none. Name it for what it reports, or settle the "
            + $"hide-decorative-elements convention first:\n  "
            + string.Join("\n  ", unnamedOverAllowance));

        Assert.True(sameOnEveryRow.Count == 0,
            "these progress bars are drawn once per item but announce a constant, so every row reports "
            + "the same words and none of them says which item it belongs to. Bind a property of the "
            + $"row:\n  " + string.Join("\n  ", sameOnEveryRow));
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

    /// <summary>
    /// Every machine-wide Run key the scan enumerates must have its enable/disable state read from, and
    /// written to, the matching <c>StartupApproved</c> subkey.
    /// <para>Windows keeps the disabled-state of a <c>Wow6432Node\...\Run</c> item under
    /// <c>StartupApproved\Run32</c>, not <c>StartupApproved\Run</c>. Mapping a 32-bit entry to the 64-bit
    /// approved key puts the disable blob where Windows never looks: the item keeps running at every boot
    /// while the tab reports "Disabled". That exact failure already shipped once for the all-users startup
    /// folder, which is why <c>CommonStartupFolder</c> exists as its own source — this pins the same
    /// contract for the 32-bit registry view so the pattern cannot be reintroduced by adding a key to the
    /// array and reusing the nearest source value.</para>
    /// <para>Asserted at source level because <c>SetEnabledAsync</c> writes to the live registry, so the
    /// mapping cannot be exercised from a unit test without touching the user's real machine.</para>
    /// </summary>
    [Fact]
    public void EveryMachineRunKey_ReadsAndWritesItsOwnStartupApprovedKey()
    {
        var source = File.ReadAllText(Path.Combine(FindAppProjectDir(), "Services", "StartupService.cs"));

        // The 32-bit Run key must actually be enumerated. Without this the rest of the guard would pass
        // on a scan that never produces a 32-bit entry at all — which is precisely the pre-fix state.
        //
        // Matched as the whole TUPLE, not the path alone. The bare path is a PREFIX of the RunOnce path on
        // the very next line, so Contains(@"…\CurrentVersion\Run") stayed satisfied by the RunOnce row even
        // with the Run row deleted — and RunOnce is explicitly undisableable (SetEnabledAsync refuses it),
        // so this guard would have passed while the only disableable 32-bit key was gone. Found by an
        // adversarial audit of the guard itself; reasoning about the two 32-bit and 64-bit paths missed it,
        // because the collision is with the neighbouring RunOnce row rather than the other bitness.
        Assert.Contains(
            @"(@""SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run"", StartupSource.RegistryLocalMachine32)",
            source, StringComparison.Ordinal);

        // Read path: ApplyApprovedState must map the 32-bit source to the Run32 dictionary, and must NOT
        // fall back between the two views — "hklmApproved ?? hklm32Approved" reads the wrong key whenever
        // both exist.
        var readAt = source.IndexOf("private static void ApplyApprovedState", StringComparison.Ordinal);
        Assert.True(readAt > 0, "ApplyApprovedState was not found — fix this guard, do not delete it.");
        var writeAt = source.IndexOf("public static async Task<bool> SetEnabledAsync", StringComparison.Ordinal);
        Assert.True(writeAt > readAt,
            "SetEnabledAsync is expected after ApplyApprovedState; the slices below assume that order.");

        var readBody = source[readAt..writeAt];
        Assert.Contains("StartupSource.RegistryLocalMachine32 => hklm32Approved", readBody, StringComparison.Ordinal);
        Assert.Contains("StartupSource.RegistryLocalMachine => hklmApproved,", readBody, StringComparison.Ordinal);
        // Match the ARM of the switch, not the bare phrase: the comment above the 32-bit arm explains the
        // old fallback by naming it, and a guard that forbids its own explanation goes red on the fixed
        // tree (that mistake was made once already, in the release-workflow guard).
        Assert.DoesNotContain(
            "StartupSource.RegistryLocalMachine => hklmApproved ?? hklm32Approved",
            readBody, StringComparison.Ordinal);

        // Write path: the same source must target ApprovedRun32HKLM.
        var writeBody = source[writeAt..];
        Assert.Contains(
            "StartupSource.RegistryLocalMachine32 => (Registry.LocalMachine, ApprovedRun32HKLM)",
            writeBody, StringComparison.Ordinal);
        Assert.Contains(
            "StartupSource.RegistryLocalMachine => (Registry.LocalMachine, ApprovedRunHKLM)",
            writeBody, StringComparison.Ordinal);

        // The approved key must be CREATED if absent, not merely opened. Windows creates each
        // StartupApproved subkey lazily, on the first disable through that list, so on a machine where
        // nothing has ever been disabled the key does not exist — OpenSubKey(writable: true) returns null
        // and disabling failed with "StartupApproved key not found" on exactly the machines most likely to
        // need it. Verified against the live registry while fixing it: OpenSubKey on a missing key returns
        // null, CreateSubKey returns a handle and creates it.
        //
        // Matched inside the write body and by CALL SHAPE, so the explanatory comment above the call — which
        // necessarily names OpenSubKey to explain what was wrong — cannot satisfy or break this assertion.
        Assert.Contains("root.CreateSubKey(approvedPath)", writeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("root.OpenSubKey(approvedPath", writeBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// A startup source with NO <c>StartupApproved</c> state must be refused outright, never allowed to
    /// fall through to the approved-key switch.
    /// <para>The guard above pins the sources whose state lives in a DIFFERENT key. This pins the harder
    /// case: the policy Run key (<c>…\CurrentVersion\Policies\Explorer\Run</c>) has no approved state
    /// anywhere, because Windows never consults <c>StartupApproved</c> for a policy key. Letting such an
    /// entry reach the switch writes a disable blob to <c>StartupApproved\Run</c>, which nothing reads —
    /// the item keeps starting at every boot while the tab reports "Disabled". That failure has already
    /// shipped twice in this service's history (the all-users folder, then the 32-bit view), and RunOnce is
    /// refused for precisely this reason.</para>
    /// <para>Asserted at source level for the same reason as the guard above: <c>SetEnabledAsync</c> writes
    /// to the live registry, so the refusal cannot be exercised from a unit test without touching the
    /// user's real machine.</para>
    /// </summary>
    [Fact]
    public void EveryStartupSourceWithNoApprovedKey_IsRefusedInsteadOfFakingSuccess()
    {
        var source = File.ReadAllText(Path.Combine(FindAppProjectDir(), "Services", "StartupService.cs"));

        // The key must actually be enumerated, or everything below would hold over a scan that never
        // produces a policy entry — the pre-fix state, and a guard passing on the defect.
        Assert.Contains("private const string PolicyRunKey =", source, StringComparison.Ordinal);
        Assert.Contains(
            @"@""SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run""",
            source, StringComparison.Ordinal);

        // Both hives: the key exists under HKCU and HKLM, and bundleware writes whichever it can.
        foreach (var hive in new[] { "Registry.CurrentUser", "Registry.LocalMachine" })
        {
            Assert.Contains(
                $"ReadRunKey({hive}, PolicyRunKey, StartupSource.PolicyRun, results)",
                source, StringComparison.Ordinal);
        }

        var writeAt = source.IndexOf("public static async Task<bool> SetEnabledAsync", StringComparison.Ordinal);
        Assert.True(writeAt > 0, "SetEnabledAsync was not found — fix this guard, do not delete it.");
        var writeBody = source[writeAt..];

        // The refusal must come BEFORE the approved-key switch, so slice there and require it in the
        // earlier half: a refusal placed after the switch would never run.
        var switchAt = writeBody.IndexOf("var (root, approvedPath) = entry.Source switch", StringComparison.Ordinal);
        Assert.True(switchAt > 0, "the approved-key switch was not found in SetEnabledAsync.");
        var beforeSwitch = writeBody[..switchAt];

        Assert.Contains("entry.Source == StartupSource.PolicyRun", beforeSwitch, StringComparison.Ordinal);

        // And it must actually return false, not merely set a message and carry on into the switch.
        var refusalAt = beforeSwitch.IndexOf("entry.Source == StartupSource.PolicyRun", StringComparison.Ordinal);
        Assert.Contains("return false;", beforeSwitch[refusalAt..], StringComparison.Ordinal);

        // The read path must not map the policy source to an approved dictionary either. Matched as the
        // switch-ARM shape so the comments that name the source cannot satisfy it.
        var readAt = source.IndexOf("private static void ApplyApprovedState", StringComparison.Ordinal);
        Assert.True(readAt > 0 && readAt < writeAt,
            "ApplyApprovedState was not found before SetEnabledAsync; the slice below assumes that order.");
        Assert.DoesNotContain("StartupSource.PolicyRun =>", source[readAt..writeAt], StringComparison.Ordinal);
    }

    /// <summary>
    /// Performance Mode must refuse to CAPTURE a recovery baseline while a game profile is live — and must
    /// still be allowed to LOAD one that was persisted earlier.
    /// <para>The lock added for #1501 stops the two tabs from snapshotting each other mid-change, but it is
    /// held per operation and a gaming session outlives it: the profile applies, releases the lock, and its
    /// power plan and visual-effects values stay live until the game exits. A capture in that window
    /// records borrowed values as the user's own and persists them, so a later Restore All puts the machine
    /// on a gaming plan it was never on.</para>
    /// <para>Both halves are asserted because each can be broken on its own. Moving the check after
    /// <c>TakeSnapshotAsync</c> would let the capture happen and merely refuse afterwards; moving it above
    /// <c>LoadSnapshot</c> would block a legitimate load of a baseline that PREDATES the session, which
    /// would turn a safety guard into "Performance Mode stops working while a game runs".</para>
    /// </summary>
    [Fact]
    public void PerformanceMode_RefusesToCaptureABaselineDuringAGameSession()
    {
        var vm = File.ReadAllText(
            Path.Combine(FindAppProjectDir(), "ViewModels", "PerformanceViewModel.cs"));

        // Comment-stripped: the paragraph above the check names both TakeSnapshotAsync and the session,
        // and a positional assertion that reads prose reports the ordering backwards (batch 106).
        var slice = WithoutComments(MemberSlice(vm, "private async Task EnsureSnapshotAsync()"));
        Assert.True(slice.Length > 300,
            $"the EnsureSnapshotAsync slice is {slice.Length} chars — too short to be the method, so every "
            + "assertion below would measure nothing.");

        var checkAt = slice.IndexOf("_gaming.IsActive", StringComparison.Ordinal);
        var loadAt = slice.IndexOf("LoadSnapshot", StringComparison.Ordinal);
        var captureAt = slice.IndexOf("TakeSnapshotAsync", StringComparison.Ordinal);

        var offenders = new List<string>();

        if (checkAt < 0)
            offenders.Add("EnsureSnapshotAsync does not consult the gaming session at all, so it can record "
                + "a profile's power plan as the user's original");
        Assert.True(loadAt >= 0 && captureAt >= 0,
            "LoadSnapshot / TakeSnapshotAsync were not both found in EnsureSnapshotAsync — this guard "
            + "cannot check the ordering it exists for; fix the guard.");

        if (checkAt >= 0 && checkAt > captureAt)
            offenders.Add("the gaming-session check sits AFTER TakeSnapshotAsync, so the borrowed settings "
                + "are captured first and only then refused");

        if (checkAt >= 0 && checkAt < loadAt)
            offenders.Add("the gaming-session check sits BEFORE LoadSnapshot, which blocks loading a "
                + "baseline persisted before the session began — that is not a safety win, it just stops "
                + "Performance Mode working while a game runs");

        // The refusal must carry an instruction, not just fail. Every Apply command surfaces
        // InvalidOperationException.Message verbatim in StatusMessage.
        if (!vm.Contains("Stop the game profile first", StringComparison.Ordinal))
            offenders.Add("the refusal no longer tells the user what to do; the message is what reaches "
                + "StatusMessage, so an empty or generic one leaves them stuck");

        // One gaming service for the whole designer/test graph. Under DI both view-models resolve the
        // same singleton, but the manual graph can silently hand Performance Mode its own copy — which
        // would answer "no session" while the real one had a game running, i.e. exactly the state that
        // must never be snapshotted, with the guard above still passing.
        var shell = WithoutComments(File.ReadAllText(
            Path.Combine(FindAppProjectDir(), "ViewModels", "MainWindowViewModel.cs")));
        var built = shell.Split("new GamingProfileService(").Length - 1;
        if (built != 1)
            offenders.Add($"MainWindowViewModel constructs GamingProfileService {built} times; the designer "
                + "graph must build exactly one and pass it to both Performance Mode and Gaming Profile");

        Assert.True(offenders.Count == 0,
            "the Performance Mode baseline contract is broken:\n  - " + string.Join("\n  - ", offenders));
    }

    /// <summary>
    /// Every tab must have exactly ONE name: the label in the sidebar and the header on the page must
    /// read the same.
    /// <para>Five of 58 pairs disagreed — "App Alerts" under a page headed "App Installation Alerts",
    /// "Profile Export/Import" under "Profile Export / Import". For the target persona a mismatch is a
    /// small dose of doubt about having clicked the right thing, and it costs nothing to remove. The point
    /// of asserting it is that the invariant survives the NEXT tab: a label and a header are edited in two
    /// different files, so they drift silently, and nothing else in the build compares them.</para>
    /// <para>Three pairs are deliberate and listed with the reason and the issue that owns them. An
    /// exception must state the exact header it expects, so a tab in the list is still pinned — it just
    /// pins a different value — and a tab whose mismatch is silently "fixed" fails until its entry is
    /// removed.</para>
    /// <para>Header text is HTML-decoded before comparison. Without that, <c>DNS &amp;amp; Hosts</c> in
    /// XAML reads as a mismatch against the label <c>DNS &amp; Hosts</c>, which is the same text — a
    /// first pass at this check reported 8 mismatches, and 3 of them were that artifact.</para>
    /// </summary>
    [Fact]
    public void EveryTabsSidebarLabel_MatchesItsPageHeader()
    {
        // nav id -> (the header it is allowed to differ with, why).
        var tolerated = new Dictionary<string, (string Header, string Why)>(StringComparer.Ordinal)
        {
            ["nav-about"] = ("About SysManager",
                "\"About\" is the universal convention for the sidebar and the expanded header is standard; "
                + "forcing parity here would be churn for its own sake (#1516 says so explicitly)"),
            ["nav-context-menu"] = ("Context Menu Manager",
                "the label is proposed to become \"Right-Click Menu\" in a separate rename issue; aligning "
                + "it to \"Context Menu Manager\" now would have to be undone by that change"),
            ["nav-privacy-monitor"] = ("Privacy Monitor",
                "the Camera/Mic/Location naming is owned by its own issue, which decides both sides at once"),
        };

        var vm = File.ReadAllText(Path.Combine(FindAppProjectDir(), "ViewModels", "MainWindowViewModel.cs"));
        var nav = MemberSlice(vm, "private NavGroup[] BuildNavGroups()");
        Assert.True(nav.Length > 2000,
            $"the BuildNavGroups slice is {nav.Length} chars — too short to hold 58 tabs, so this guard "
            + "would compare almost nothing.");

        var entries = NavEntry().Matches(nav).Cast<Match>().ToArray();
        Assert.True(entries.Length >= 50,
            $"only {entries.Length} nav entries parsed — the shape of BuildNavGroups changed and this "
            + "guard is no longer reading the sidebar; fix the pattern rather than trusting the pass.");

        var offenders = new List<string>();
        var compared = 0;

        foreach (var entry in entries)
        {
            var navId = entry.Groups[1].Value;
            var label = entry.Groups[2].Value;
            var view = entry.Groups[3].Value;

            var path = Path.Combine(FindAppProjectDir(), "Views", view + ".xaml");
            Assert.True(File.Exists(path), $"{view}.xaml is referenced by {navId} but does not exist");

            // Comments stripped so a commented-out old header cannot be read as the live one.
            var xaml = XmlComment().Replace(File.ReadAllText(path), string.Empty);
            var displayAt = xaml.IndexOf("Style=\"{StaticResource Display}\"", StringComparison.Ordinal);
            Assert.True(displayAt > 0,
                $"{view}.xaml has no Display-styled header — every page carries one, so either the view "
                + "regressed or this guard is looking for the wrong marker.");

            // Bound the search to the element that carries the style, so Text= from a neighbouring
            // control cannot be mistaken for the header. Attribute order is irrelevant this way.
            var open = xaml.LastIndexOf('<', displayAt);
            var close = xaml.IndexOf('>', displayAt);
            Assert.True(open >= 0 && close > open, $"could not bound the header element in {view}.xaml");
            var textMatch = TextAttribute().Match(xaml[open..close]);
            Assert.True(textMatch.Success, $"the Display-styled element in {view}.xaml has no Text=");

            var header = System.Net.WebUtility.HtmlDecode(textMatch.Groups[1].Value);
            compared++;

            if (tolerated.TryGetValue(navId, out var exception))
            {
                if (!string.Equals(header, exception.Header, StringComparison.Ordinal))
                    offenders.Add($"{navId} is a documented exception expecting the header "
                        + $"\"{exception.Header}\" but the page now says \"{header}\". Either restore it or "
                        + $"remove the entry — the reason on file is: {exception.Why}");
                else if (string.Equals(header, label, StringComparison.Ordinal))
                    offenders.Add($"{navId} now matches its header (\"{header}\") but is still listed as an "
                        + "exception. Delete the entry so the list keeps describing the app.");
                continue;
            }

            if (!string.Equals(header, label, StringComparison.Ordinal))
                offenders.Add($"{navId}: the sidebar says \"{label}\" but the page is headed \"{header}\" — "
                    + "one tab, two names. Align them, or add a documented exception saying why not.");
        }

        Assert.True(compared >= 50,
            $"only {compared} label/header pairs were actually compared out of {entries.Length} entries.");

        Assert.True(offenders.Count == 0,
            "every tab must have one name:\n  - " + string.Join("\n  - ", offenders));
    }

    // Tab<TVm>("nav-id", "Label", typeof(Views.SomeView)  /  EagerItem("nav-id", "Label", typeof(Views.SomeView)
    [GeneratedRegex(@"(?:Tab<\w+>|EagerItem)\(\s*""([^""]+)""\s*,\s*""([^""]+)""\s*,\s*typeof\(Views\.(\w+)\)",
                    RegexOptions.Compiled)]
    private static partial Regex NavEntry();

    [GeneratedRegex(@"Text=""([^""]*)""", RegexOptions.Compiled)]
    private static partial Regex TextAttribute();

    /// <summary>
    /// Gaming Profile must take the app-wide system-modification lock, and the two directions must stay
    /// asymmetric: apply REFUSES when the lock is held, revert NEVER does.
    /// <para>Gaming Profile and Performance Mode write the same power plan and the same visual-effects
    /// flag, and each keeps its own record of the original — <c>gaming-profiles.json</c> versus
    /// <c>performance-snapshot.json</c>. The service had its own <c>_gate</c>, which serialises it against
    /// itself and says nothing about the other tab. The damage is done at SNAPSHOT time: a snapshot taken
    /// while the other tab's change is live records that change as the baseline, and the later "restore"
    /// strands the machine off the user's real power plan with both tabs believing they were correct. So
    /// the acquire has to sit before <c>CaptureSnapshotAsync</c> — around the writes alone would leave the
    /// hazard open — and position is asserted here, not just presence.</para>
    /// <para>The opposite rule holds for undoing. <c>RevertAsync</c> runs from the game's
    /// <c>Process.Exited</c> callback and <c>RecoverPendingAsync</c> from startup after a crash; refusing
    /// either leaves tweaks live with nothing left to undo them, which is worse than the contention the
    /// lock prevents. Neither captures a snapshot, so running unlocked cannot poison a baseline. A future
    /// edit that "tidies" these into the same refuse-on-busy shape as apply would reintroduce exactly that,
    /// so the null-lock branch is asserted to warn and continue rather than return.</para>
    /// </summary>
    [Fact]
    public void GamingProfileMutations_TakeTheLockAndOnlyApplyRefuses()
    {
        var service = File.ReadAllText(
            Path.Combine(FindAppProjectDir(), "Services", "GamingProfileService.cs"));

        // Comments are stripped from every slice before anything is matched. The first draft of this
        // guard went false-RED on its own explanatory comment: the paragraph above the acquire names
        // CaptureSnapshotAsync, so the positional check compared the acquire against a MENTION of the
        // snapshot rather than the call, and reported the ordering backwards. A source-text guard must
        // read code, never prose — including its own.
        var apply = WithoutComments(MemberSlice(service, "public async Task<GamingApplyResult> ApplyAsync"));
        var revert = WithoutComments(MemberSlice(service, "public async Task RevertAsync"));
        var recover = WithoutComments(MemberSlice(service, "public async Task RecoverPendingAsync"));
        foreach (var (name, slice) in new[] { ("ApplyAsync", apply), ("RevertAsync", revert), ("RecoverPendingAsync", recover) })
            Assert.True(slice.Length > 200,
                $"the {name} slice is {slice.Length} chars — too short to be the method, so the assertions "
                + "below would measure nothing.");

        var offenders = new List<string>();

        // ── Apply: the acquire must precede the snapshot, and refusal must be reported ──
        var acquireAt = apply.IndexOf("OperationCategory.SystemModification", StringComparison.Ordinal);
        var snapshotAt = apply.IndexOf("CaptureSnapshotAsync", StringComparison.Ordinal);
        if (acquireAt < 0)
            offenders.Add("ApplyAsync does not take the SystemModification lock at all");
        else if (snapshotAt < 0)
            offenders.Add("CaptureSnapshotAsync was not found in ApplyAsync — this guard cannot check the "
                + "ordering it exists to check; fix the guard.");
        else if (acquireAt > snapshotAt)
            offenders.Add("ApplyAsync takes the lock AFTER CaptureSnapshotAsync, so the snapshot can still "
                + "record Performance Mode's applied state as the baseline — the whole point of the lock");

        // "BlockedBy:" — the named argument — not bare "BlockedBy", which the log template
        // "{BlockedBy} already holds..." would satisfy on its own. Comments are stripped above but
        // string literals are not, and a guard that a log message can satisfy proves nothing about
        // what the method returns.
        if (!apply.Contains("BlockedBy:", StringComparison.Ordinal))
            offenders.Add("ApplyAsync never returns BlockedBy, so a refused start cannot be told apart "
                + "from one that applied nothing");

        // ── Revert paths: acquire, then warn and CONTINUE on a null lock ──
        foreach (var (name, slice) in new[] { ("RevertAsync", revert), ("RecoverPendingAsync", recover) })
        {
            if (!slice.Contains("OperationCategory.SystemModification", StringComparison.Ordinal))
            {
                offenders.Add($"{name} does not take the SystemModification lock, so it does not serialise "
                    + "with Performance Mode even when the lock is free");
                continue;
            }

            var nullCheck = slice.IndexOf("opLock is null", StringComparison.Ordinal);
            if (nullCheck < 0)
            {
                offenders.Add($"{name} never handles a null lock — TryAcquire returning null must be an "
                    + "explicit, logged decision to continue, not an ignored value");
                continue;
            }

            // The whole statement that follows the null check: it must log and fall through.
            var statementEnd = slice.IndexOf(';', nullCheck);
            var branch = statementEnd > nullCheck ? slice[nullCheck..statementEnd] : slice[nullCheck..];
            if (branch.Contains("return", StringComparison.Ordinal))
                offenders.Add($"{name} returns early when the lock is busy. Undoing must never be refused: "
                    + "the game has already exited, so the tweaks would stay live with nothing to revert "
                    + "them. Warn and continue.");
            if (!branch.Contains("Log.Warning", StringComparison.Ordinal))
                offenders.Add($"{name} continues without the lock but does not warn — a silent unlocked "
                    + "system change is exactly what a maintainer needs to see in the log");
        }

        // ── The README claim must match the code, in both directions ──
        var readme = Collapse(File.ReadAllText(Path.Combine(FindRepoRoot(), "README.md")));
        var lockSectionAt = readme.IndexOf("### Operation Lock", StringComparison.Ordinal);
        Assert.True(lockSectionAt > 0, "README.md has no '### Operation Lock' section — the guard would "
            + "pass vacuously.");
        var after = readme[(lockSectionAt + 1)..];
        var end = after.IndexOf("### ", StringComparison.Ordinal);
        var lockSection = end > 0 ? after[..end] : after;
        Assert.True(lockSection.Length > 200,
            $"the README Operation Lock section sliced to {lockSection.Length} chars — not the section.");

        var takesLock = acquireAt >= 0;
        var listed = lockSection.Contains("Gaming Profile", StringComparison.Ordinal);
        if (takesLock && !listed)
            offenders.Add("Gaming Profile takes the lock but the README Operation Lock section does not "
                + "list it — the section claims the lock covers every tab that mutates system state");
        if (!takesLock && listed)
            offenders.Add("the README lists Gaming Profile under Operation Lock but the service no longer "
                + "takes it — the claim is now false");

        Assert.True(offenders.Count == 0,
            "the Gaming Profile lock contract is broken:\n  - " + string.Join("\n  - ", offenders));
    }

    /// <summary>
    /// C# source with comments removed, so a source-text assertion cannot be satisfied — or defeated —
    /// by prose that merely names the construct. Quote-aware on the line scan so a <c>//</c> inside a
    /// string literal (a URL, say) is left alone.
    /// </summary>
    private static string WithoutComments(string source)
    {
        var noBlocks = BlockComment().Replace(source, " ");
        var kept = new List<string>();
        foreach (var line in noBlocks.Split('\n'))
        {
            var inString = false;
            var cut = -1;
            for (var i = 0; i < line.Length - 1; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) inString = !inString;
                else if (!inString && line[i] == '/' && line[i + 1] == '/') { cut = i; break; }
            }
            kept.Add(cut >= 0 ? line[..cut] : line);
        }
        return string.Join("\n", kept);
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    /// <summary>
    /// A member's text from its declaration up to the next member at the same indentation — enough to
    /// assert what one method does without matching an identical call elsewhere in the file.
    /// </summary>
    private static string MemberSlice(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        if (start < 0) return "";
        var rest = source[start..];
        var end = NextMemberDeclaration().Match(rest, 1);
        return end.Success ? rest[..end.Index] : rest;
    }

    /// <summary>
    /// A registry path must be declared in ONE place. Two verbatim copies of the same key drift, and they
    /// drift silently: nothing tells the compiler that two identical strings were meant to stay identical.
    /// <para>The defect this generalises: <c>PushNotifications\ToastEnabled</c> was declared twice,
    /// byte-identical, in <c>NotificationBlockerService</c> and in the Gaming Profile's
    /// <c>NotificationsTweak</c> — and BOTH wrote it, so the Notifications tab and Gaming Profile were
    /// fighting over one switch with neither aware of the other. The repo had already learned this lesson
    /// and written it down: <c>Helpers/WingetId.cs</c> exists because the winget-ID allowlist had been
    /// copy-pasted into three services "where three copies could drift apart".</para>
    /// <para>Nine duplicate pairs predate this guard and are listed with the reason each is tolerated, so
    /// the rule can be enforced now rather than after a cleanup that may never happen. The allowlist is
    /// keyed to the EXACT set of files, not to a count and not to the literal alone: adding a THIRD copy of
    /// an already-tolerated path fails, and so does fixing a pair without deleting its entry — a stale
    /// exemption is a lie about the codebase that the next reader will trust.</para>
    /// </summary>
    [Fact]
    public void NoRegistryPath_IsDeclaredInTwoPlaces()
    {
        // literal (lower-cased) -> the only files allowed to declare it, and why.
        var tolerated = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // A Windows-defined device class GUID, not a SysManager convention. Both readers want the
            // display-adapters class; neither owns it.
            [@"system\currentcontrolset\control\class\{4d36e968-e325-11ce-bfc1-08002be10318}"] =
                "Helpers/GpuVramHelper.cs,Services/PerformanceService.cs",

            // The two Uninstall roots. AppAlertService watches them for newly installed apps;
            // UninstallerService enumerates them to list programs. Same roots, different jobs.
            [@"software\microsoft\windows\currentversion\uninstall"] =
                "Services/AppAlertService.cs,Services/UninstallerService.cs",
            [@"software\wow6432node\microsoft\windows\currentversion\uninstall"] =
                "Services/AppAlertService.cs,Services/UninstallerService.cs",

            // SettingsWatchdogService re-declares the policy keys PrivacyService writes, so it can notice
            // when Windows silently reverts them. Watching your own writes needs the same path twice, but
            // it is still duplication: correct one and not the other and the watchdog quietly stops
            // watching the toggle it is named after.
            [@"hklm\software\policies\microsoft\windows\datacollection"] =
                "Services/PrivacyService.cs,Services/SettingsWatchdogService.cs",
            [@"hklm\software\policies\microsoft\windows\system"] =
                "Services/PrivacyService.cs,Services/SettingsWatchdogService.cs",
            [@"hkcu\software\microsoft\windows\currentversion\advertisinginfo"] =
                "Services/PrivacyService.cs,Services/SettingsWatchdogService.cs",
            [@"hkcu\software\microsoft\windows\currentversion\contentdeliverymanager"] =
                "Services/PrivacyService.cs,Services/SettingsWatchdogService.cs",
            [@"hkcu\software\policies\microsoft\windows\explorer"] =
                "Services/PrivacyService.cs,Services/SettingsWatchdogService.cs",
            [@"hklm\software\policies\microsoft\dsh"] =
                "Services/PrivacyService.cs,Services/SettingsWatchdogService.cs",
        };

        var appDir = FindAppProjectDir();
        var sources = Directory
            .EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                    StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                       StringComparison.Ordinal))
            .ToArray();

        Assert.True(sources.Length >= 50,
            $"only {sources.Length} app source files found under {appDir} — the scan is not seeing the "
            + "codebase, so every assertion below would pass vacuously.");

        var byLiteral = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var file in sources)
        {
            var relative = Path.GetRelativePath(appDir, file).Replace(Path.DirectorySeparatorChar, '/');
            foreach (var match in VerbatimLiteral().Matches(File.ReadAllText(file)).Cast<Match>())
            {
                var value = match.Groups[1].Value;
                // Registry-shaped only: a separator plus a hive or a well-known registry segment. The
                // length floor drops fragments like @"Software\" that are composed at the call site.
                if (!value.Contains('\\', StringComparison.Ordinal) || value.Length < 12) continue;
                if (!RegistryShaped().IsMatch(value)) continue;

                var key = value.ToLowerInvariant();
                if (!byLiteral.TryGetValue(key, out var files))
                    byLiteral[key] = files = new SortedSet<string>(StringComparer.Ordinal);
                files.Add(relative);
            }
        }

        Assert.True(byLiteral.Count >= 30,
            $"only {byLiteral.Count} registry-shaped literals matched — the extraction is broken, so a "
            + "clean result would mean nothing.");

        var offenders = new List<string>();

        foreach (var (literal, files) in byLiteral.Where(kv => kv.Value.Count > 1))
        {
            var actual = string.Join(",", files);
            if (!tolerated.TryGetValue(literal, out var allowed))
            {
                offenders.Add($"NEW duplicate: {literal} — declared in {actual}. Give it one owner and "
                    + "reference it from there (see Helpers/WingetId.cs).");
                continue;
            }

            if (!string.Equals(actual, allowed, StringComparison.Ordinal))
                offenders.Add($"the tolerated pair {literal} moved: expected {allowed}, found {actual}. A "
                    + "third copy is never acceptable; edit the entry only if the set genuinely changed.");
        }

        foreach (var (literal, allowed) in tolerated)
        {
            if (byLiteral.TryGetValue(literal, out var files) && files.Count > 1) continue;
            offenders.Add($"{literal} is listed as a tolerated duplicate but is no longer declared twice "
                + $"(expected {allowed}). Delete the entry — it now exempts nothing.");
        }

        Assert.True(offenders.Count == 0,
            "registry paths must have exactly one declaration site:\n  - " + string.Join("\n  - ", offenders));
    }

    [GeneratedRegex(@"@""([^""]*)""", RegexOptions.Compiled)]
    private static partial Regex VerbatimLiteral();

    [GeneratedRegex(@"SOFTWARE|Software|SYSTEM|System|HKEY|HKCU|HKLM|CurrentVersion|Policies|Classes",
                    RegexOptions.Compiled)]
    private static partial Regex RegistryShaped();

    /// <summary>
    /// The Startup Manager copy must describe the list its scan actually produces, and the two tabs that
    /// read the same scheduled tasks must point at each other.
    /// <para>README said the tab lists "logon-triggered scheduled tasks". <c>ReadScheduledTasks</c> requires
    /// only a non-empty <c>Triggers</c> blob and never decodes the trigger TYPE, so a task that runs daily
    /// or on idle is listed identically — the qualifier was simply untrue, and untrue in the way a reviewer
    /// waves through, because it describes what the tab sounds like it ought to do. The issue asking for
    /// this fix repeated the same phrase, so writing the copy from the issue text would have shipped the
    /// error a second time.</para>
    /// <para>The other half is scope. The scan drops <c>\Microsoft\</c> and <c>\Windows\</c> tasks on
    /// purpose — the short list is the right answer to "why is my PC slow to start" — so the tab is
    /// deliberately incomplete and has to say so, and to name the tab that is complete. Both directions are
    /// asserted, because a third-party task appears on BOTH tabs and both toggle it through the same
    /// <c>schtasks /Change</c> call: a user who disables it in one must not read the other list as a
    /// different object.</para>
    /// <para>Conditional rather than a blocklist, the same shape as
    /// <see cref="NoPublicDocument_ClaimsAPublisherPinThatIsNotArmed"/>. The phrase becomes legal the day
    /// the scan really decodes a logon trigger, and the disclosure becomes WRONG the day the exclusions are
    /// dropped and the list turns complete. Either edit flips what the copy must say, and this fails until
    /// the copy follows.</para>
    /// </summary>
    [Fact]
    public void TheStartupTabCopy_DescribesTheTaskScanItActuallyRuns()
    {
        var appDir = FindAppProjectDir();
        var service = File.ReadAllText(Path.Combine(appDir, "Services", "StartupService.cs"));

        // Slice the scan itself, so a mention anywhere else in this 900-line service cannot stand in for it.
        var scanAt = service.IndexOf("private static void ReadScheduledTasks", StringComparison.Ordinal);
        Assert.True(scanAt > 0, "ReadScheduledTasks was not found in StartupService.cs — fix this guard "
            + "rather than trusting its pass.");
        var rest = service[scanAt..];
        var nextMember = NextMemberDeclaration().Match(rest, 1);
        var scan = nextMember.Success ? rest[..nextMember.Index] : rest;
        Assert.True(scan.Length > 200,
            $"the ReadScheduledTasks slice is {scan.Length} chars — too short to be the method, so every "
            + "assertion below would be measuring nothing.");

        // Does the scan decode the trigger TYPE, or merely require that SOME trigger exists? Positive
        // signals only: indexing the blob, or naming a trigger kind. Counting occurrences of the local
        // would go red on an unrelated rename.
        var decodesTriggerType = scan.Contains("triggers[", StringComparison.Ordinal)
            || scan.Contains("TriggerType", StringComparison.Ordinal)
            || scan.Contains("LogonTrigger", StringComparison.Ordinal)
            || scan.Contains("TASK_TRIGGER", StringComparison.Ordinal);

        // Is the list deliberately incomplete? Matched as the real StartsWith arguments, so the comments
        // that explain the exclusion cannot satisfy it.
        var exclusions = new[] { @"@""\Microsoft\""", @"@""\Windows\""" }
            .Count(marker => scan.Contains(marker, StringComparison.Ordinal));
        Assert.Equal(2, exclusions);

        var views = Path.Combine(appDir, "Views");
        // Comments stripped: a comment naming the other tab must not count as telling the user about it.
        // The neighbouring guard's first draft stayed green for exactly that reason.
        var startupView = Collapse(XmlComment().Replace(
            File.ReadAllText(Path.Combine(views, "StartupView.xaml")), string.Empty));
        var taskView = Collapse(XmlComment().Replace(
            File.ReadAllText(Path.Combine(views, "TaskSchedulerView.xaml")), string.Empty));

        var root = FindRepoRoot();
        var readme = Collapse(File.ReadAllText(Path.Combine(root, "README.md")));

        // CHANGELOG is deliberately NOT scanned. It is the historical record, so the entry that documents
        // this very fix has to be free to quote the wording being removed.
        var offenders = new List<string>();

        if (!decodesTriggerType)
        {
            foreach (var (surface, text) in new[]
                     { ("README.md", readme), ("StartupView.xaml", startupView), ("TaskSchedulerView.xaml", taskView) })
            {
                var claim = LogonTriggerClaim().Match(text);
                if (claim.Success)
                    offenders.Add($"{surface} claims \"{claim.Value}\" but the scan only checks that a "
                        + "trigger EXISTS — it never reads which kind");
            }
        }

        if (exclusions == 2)
        {
            if (!startupView.Contains("Task Scheduler", StringComparison.Ordinal))
                offenders.Add("StartupView.xaml never names Task Scheduler, so the tab hides Windows' own "
                    + "tasks without telling the user where the complete list is");

            if (!taskView.Contains("Startup Manager", StringComparison.Ordinal))
                offenders.Add("TaskSchedulerView.xaml never names Startup Manager, so a user who disabled a "
                    + "task there cannot tell it is the same task");

            // Scoped to the tab's own README section: a mention under any other heading is not this
            // tab explaining itself.
            var sectionAt = readme.IndexOf("### Startup Manager", StringComparison.Ordinal);
            Assert.True(sectionAt > 0, "README.md has no '### Startup Manager' section — the guard would "
                + "pass vacuously.");
            var after = readme[(sectionAt + 1)..];
            var sectionEnd = after.IndexOf("### ", StringComparison.Ordinal);
            var section = sectionEnd > 0 ? after[..sectionEnd] : after;
            Assert.True(section.Length > 200,
                $"the README Startup Manager section sliced to {section.Length} chars — not the section.");

            if (!section.Contains("Task Scheduler", StringComparison.Ordinal))
                offenders.Add("the README Startup Manager section does not point at Task Scheduler, though "
                    + "the scan drops every Windows task");
        }

        // The ARCHITECTURE count is spelled out in prose, which is exactly what drifts: it was written when
        // there were four sources and had to be re-derived twice since. Pin it to the enum.
        var model = File.ReadAllText(Path.Combine(appDir, "Models", "StartupEntry.cs"));
        var enumAt = model.IndexOf("public enum StartupSource", StringComparison.Ordinal);
        Assert.True(enumAt > 0, "StartupSource was not found — the count below would be invented.");
        // Comments come off BEFORE the braces are matched: a doc comment containing a brace would
        // otherwise truncate the body and undercount the sources without failing anything.
        var declaration = DocComment().Replace(model[enumAt..], string.Empty);
        var enumBody = declaration[(declaration.IndexOf('{', StringComparison.Ordinal) + 1)..];
        enumBody = enumBody[..enumBody.IndexOf('}', StringComparison.Ordinal)];
        var sources = enumBody
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(v => v.Length > 0);
        Assert.True(sources >= 5, $"only {sources} StartupSource values parsed — the parse is wrong.");

        string[] spelled = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
        var architecture = Collapse(File.ReadAllText(Path.Combine(root, "ARCHITECTURE.md")));
        var expected = $"{spelled[sources]} kinds of location";
        if (!architecture.Contains(expected, StringComparison.Ordinal))
            offenders.Add($"ARCHITECTURE.md does not say \"{expected}\" though StartupSource now has "
                + $"{sources} values");

        Assert.True(offenders.Count == 0,
            "the Startup Manager copy no longer matches the scan behind it:\n  "
            + string.Join("\n  ", offenders));
    }

    [GeneratedRegex(@"logon[- ]triggered|triggered at logon", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LogonTriggerClaim();

    [GeneratedRegex(@"///.*?$", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex DocComment();

    /// <summary>
    /// Everything the scheduled-task query pays to fetch must reach the screen.
    /// <para>The Task Scheduler tab queried <c>Author</c>, <c>Description</c> and <c>NextRunTime</c> from
    /// Windows on every scan, carried all three through <c>ScheduledTaskInfo</c>, and even defined a
    /// formatted <c>NextRunDisplay</c> — while the view bound Name, Path, Type, State and Last run only.
    /// A user asking the one question this tab exists for, "when will this run next?", could not see the
    /// answer the app already had. This is SysManager's most persistent defect class: state that is
    /// implemented, unit-tested, and bound by nothing, which neither the compiler nor a view-model test
    /// can see — only an assertion against the shipped XAML.</para>
    /// </summary>
    [Fact]
    public void EveryScheduledTaskFieldTheQueryFetches_IsBoundInTheView()
    {
        var service = File.ReadAllText(
            Path.Combine(FindAppProjectDir(), "Services", "TaskSchedulerService.cs"));
        var view = File.ReadAllText(
            Path.Combine(FindAppProjectDir(), "Views", "TaskSchedulerView.xaml"));

        // XAML comments are stripped before matching. A comment in the view that merely NAMES a property
        // would otherwise satisfy the check — the first draft of this guard stayed green with the "Next
        // run" column deleted, because the comment above it mentioned NextRunDisplay. Only a real binding
        // counts.
        var bindings = XmlComment().Replace(view, string.Empty);

        // Only assert on fields the service genuinely asks Windows for — otherwise this guard drifts into
        // demanding UI for data that is not collected.
        //
        // Each Fetched string is matched against the SELECTION it appears in, not the bare field name. The
        // bare name was vacuous for "Author": every file in this project carries the mandatory
        // "// Author: laurentiu021 …" header, so service.Contains("Author") was satisfied by line 2 and
        // could not fail however the query changed. Found by an adversarial audit of this guard. A
        // selection fragment cannot be supplied by a comment, and stripping comments is not enough here —
        // the field genuinely appears in prose too.
        (string Fetched, string Bound)[] contract =
        [
            ("@{ n='State'; e={ [string]$_.State } }, Author, Description", "{Binding AuthorDisplay}"),
            ("Author, Description", "{Binding Description}"),
            ("Select-Object LastRunTime, NextRunTime", "{Binding NextRunDisplay}"),
            ("Select-Object LastRunTime, NextRunTime", "{Binding LastRunDisplay}"),
        ];

        var notFetched = new List<string>();
        var notBound = new List<string>();
        foreach (var (fetched, bound) in contract)
        {
            if (!service.Contains(fetched, StringComparison.Ordinal))
                notFetched.Add(fetched);
            else if (!bindings.Contains(bound, StringComparison.Ordinal))
                notBound.Add($"{fetched} -> expected a real '{bound}' in TaskSchedulerView.xaml");
        }

        // Vacuity floor: if the service stopped selecting these, the loop above would silently check
        // nothing at all and report success.
        Assert.True(notFetched.Count == 0,
            "the scheduled-task query no longer fetches these, so this guard is measuring nothing — "
            + $"fix the guard rather than trusting its pass:\n  {string.Join("\n  ", notFetched)}");

        Assert.True(notBound.Count == 0,
            "the Task Scheduler query pays to fetch these fields on every scan and the view displays "
            + "none of them, so the work is thrown away and the user cannot see data the app already "
            + $"holds:\n  {string.Join("\n  ", notBound)}");
    }

    /// <summary>
    /// The CHANGELOG's version headers must form an unbroken descending run — no version may be missing
    /// between the newest and oldest entry, and none may appear twice.
    /// </summary>
    /// <remarks>
    /// <para>Found by an audit, after three releases' notes were discovered welded into a single
    /// <c>## [1.65.10]</c> heading: 1.65.7, 1.65.8 and 1.65.9 had no heading at all, so the file jumped
    /// straight from 1.65.10 to 1.65.6 while carrying five <c>### Fixed</c> sections under one version.
    /// Anyone reading the file to find out what a release changed found nothing for three of them, and the
    /// release workflow copies each entry verbatim into the GitHub release body and the announcement — so
    /// the omission reached two public surfaces.</para>
    /// <para>Nothing caught it, because the existing gate only checks that the NEWEST entry opens with a
    /// plain-English lead. A missing middle entry is invisible to that check and to the compiler, and it is
    /// easy to cause: the mistake was appending a new entry's body without its heading while resolving a
    /// merge. A gap is mechanically detectable, so it should never need a human to notice again.</para>
    /// <para>Deliberately checks CONTIGUITY within the file rather than comparing against git tags: the
    /// test project has no git access, and a tag that was cut but never published (1.65.9) still deserves
    /// an entry, so the file's own sequence is the stronger contract.</para>
    /// <para>Scoped to 1.x on purpose, and this is a real limit rather than a convenient one. Pre-1.0
    /// development predates the release discipline: the 0.28 line alone has 35 tags against 31 entries, and
    /// there are 171 pre-1.0 entries in total. Those gaps are from a period when versions were cut by hand
    /// several times an hour; retro-writing user-facing notes for them now would be invention, not
    /// documentation. The contract this guard enforces — every released version explains itself — applies
    /// to the versions users actually download, and the boundary is stated here so nobody later reads the
    /// pass as "the whole file is contiguous".</para>
    /// </remarks>
    [Fact]
    public void TheChangelogVersionHeaders_FormAnUnbrokenDescendingRun()
    {
        var path = Path.Combine(FindRepoRoot(), "CHANGELOG.md");
        Assert.True(File.Exists(path), $"CHANGELOG.md not found at {path} — the guard would pass vacuously");

        var versions = new List<(int Major, int Minor, int Patch, string Raw, int Line)>();
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var m = ChangelogVersionHeading().Match(lines[i]);
            if (!m.Success) continue;

            var major = int.Parse(m.Groups["ma"].Value);
            if (major < 1) continue;   // pre-1.0 predates the release discipline — see the remarks

            versions.Add((major, int.Parse(m.Groups["mi"].Value),
                          int.Parse(m.Groups["pa"].Value), m.Groups["v"].Value, i + 1));
        }

        // Vacuity floor over the 1.x range this guard governs — the file carries well over a hundred such
        // entries, so a floor of 50 catches the heading pattern breaking without encoding today's count.
        Assert.True(versions.Count >= 50,
            $"only {versions.Count} 1.x CHANGELOG version headings parsed — the guard is measuring nothing, "
            + "fix it rather than trusting its pass");

        var duplicates = versions.GroupBy(v => v.Raw).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} appears {g.Count()} times (lines {string.Join(", ", g.Select(v => v.Line))})")
            .ToList();
        Assert.True(duplicates.Count == 0,
            $"a version has more than one CHANGELOG entry:\n  {string.Join("\n  ", duplicates)}");

        // Only PATCH gaps inside one minor line are checked. A minor or major bump legitimately restarts
        // the patch counter, and this project has never skipped a minor, so a rule spanning those would
        // encode history rather than a contract.
        var gaps = new List<string>();
        for (var i = 0; i < versions.Count - 1; i++)
        {
            var newer = versions[i];
            var older = versions[i + 1];
            if (newer.Major != older.Major || newer.Minor != older.Minor) continue;

            if (newer.Patch <= older.Patch)
            {
                gaps.Add($"{newer.Raw} (line {newer.Line}) is not newer than {older.Raw} "
                         + $"(line {older.Line}) — entries must descend");
                continue;
            }

            for (var missing = older.Patch + 1; missing < newer.Patch; missing++)
                gaps.Add($"{newer.Major}.{newer.Minor}.{missing} has no entry — the file jumps from "
                         + $"{newer.Raw} (line {newer.Line}) to {older.Raw} (line {older.Line})");
        }

        Assert.True(gaps.Count == 0,
            "the CHANGELOG is missing an entry for a version between two it does document. Every released "
            + "version needs its own heading and lead paragraph — the release workflow copies each entry "
            + "into the GitHub release body and the announcement, so a gap is published, not just local. If "
            + "a version was tagged but never shipped, it still gets an entry saying so:\n  "
            + string.Join("\n  ", gaps));
    }

    [GeneratedRegex(@"^## \[(?<v>(?<ma>\d+)\.(?<mi>\d+)\.(?<pa>\d+))\]", RegexOptions.Compiled)]
    private static partial Regex ChangelogVersionHeading();

    /// <summary>
    /// No bandwidth source may wrap its own sample in <c>Task.Run</c>. The offload belongs to the
    /// consumer, once, so every source is covered by construction.
    /// </summary>
    /// <remarks>
    /// <para>#1816: the 1.61.9 fix put the offload inside <c>ConnectionBandwidthSource</c> and left
    /// <c>EtwBandwidthSource</c> returning <c>Task.FromResult</c>, so precise mode still did a per-tick
    /// allocation and a two-key sort of every PID the session had ever seen on the render thread — in the
    /// mode the CHANGELOG stated was never affected. Each source was internally consistent, so nothing
    /// short of comparing them could see it, and <c>IBandwidthMonitorService</c> documents no
    /// thread-affinity contract, which is precisely why an unwritten one drifted.</para>
    /// <para>The rule is "sources stay synchronous" rather than "sources must offload" because the second
    /// version is what failed: it is satisfiable one implementor at a time. With the offload at the single
    /// consumer, a source re-adding its own is both a redundant hop and a sign someone believed the old
    /// contract — worth failing over either way.</para>
    /// </remarks>
    [Fact]
    public void EveryBandwidthSource_LeavesTheOffloadToItsConsumer()
    {
        var servicesDir = Path.Combine(FindAppProjectDir(), "Services");
        var sources = Directory.GetFiles(servicesDir, "*BandwidthSource.cs");

        // Vacuity floor: two implementors exist (connection + ETW). If the glob stops matching them, the
        // loop below inspects nothing and the guard reports success.
        Assert.True(sources.Length >= 2,
            $"only {sources.Length} bandwidth sources found in {servicesDir} — the guard is measuring "
            + "nothing, fix it rather than trusting its pass");

        var offenders = new List<string>();
        foreach (var file in sources)
        {
            var body = SampleAsyncBody(File.ReadAllText(file));
            Assert.False(body.Length == 0,
                $"{Path.GetFileName(file)}: could not locate a SampleAsync body — the guard would pass "
                + "vacuously on this file");

            if (body.Contains("Task.Run", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "these bandwidth sources offload their own sample, but BandwidthMonitorViewModel.PollOnceAsync "
            + "already wraps every source in Task.Run — a second hop per tick, and a sign the per-source "
            + "contract that let #1816 hide is creeping back. Keep SampleAsync synchronous and let the "
            + $"consumer own the offload:\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>
    /// The text of <c>SampleAsync</c> up to the next member declaration — enough to see whether the method
    /// itself offloads, without matching a <c>Task.Run</c> elsewhere in the file (a source legitimately
    /// uses one to run its ETW processing loop).
    /// </summary>
    private static string SampleAsyncBody(string source)
    {
        var start = source.IndexOf("SampleAsync(CancellationToken", StringComparison.Ordinal);
        if (start < 0) return "";

        // Stop at the next member so the slice is the method, not the rest of the class. Every following
        // member in these files opens with a doc comment or an access modifier at four-space indentation.
        var rest = source[start..];
        var end = NextMemberDeclaration().Match(rest, 1);
        return end.Success ? rest[..end.Index] : rest;
    }

    [GeneratedRegex(@"\r?\n    (?:///|private |public |internal |protected )", RegexOptions.Compiled)]
    private static partial Regex NextMemberDeclaration();

    /// <summary>
    /// Every sentence a UI test waits for must be copy the app actually ships. A UI test that quotes
    /// wording nothing renders can never pass — it is a permanently red assertion masquerading as
    /// coverage.
    /// </summary>
    /// <remarks>
    /// <para>Found by a sweep after <c>UninstallerUiTests</c> failed 1/135 on every CI run of one day,
    /// including branches that touch no app code. PR #1808 rewrote the Uninstaller's elevated banner and
    /// left the test asserting the previous sentence, which then existed nowhere in the app. Two things
    /// hid it: the branch only runs when the test session is elevated (as on the CI runner, not on a
    /// developer's box), and the <c>ui-tests</c> job is <c>continue-on-error</c>, so <c>gh pr checks</c>
    /// printed "pass" while the log said <c>Failed: 2</c>.</para>
    /// <para>This guard lives in the BLOCKING unit suite on purpose. The defect it pins is one the
    /// non-blocking UI job cannot report loudly enough to stop a merge, and it needs no desktop session
    /// to detect — it is pure text comparison over the two source trees.</para>
    /// <para>Scope is chosen by what can actually rot. A one-word wait like <c>"CPU"</c> or a fragment
    /// like <c>"drivers found"</c> survives rewording and is not checked; a multi-word PHRASE is what a
    /// copy edit breaks, so the bar is two spaces (three words) rather than a character count — the
    /// original defect's threshold experiment showed a 20-character floor excludes nearly every real
    /// call site, because the durable waits are deliberately short. Literals carrying markup, path, or
    /// format-hole characters are ids, xpaths and templates, not copy. Matching is whitespace-normalised
    /// and case-insensitive because XAML wraps attribute values across lines.</para>
    /// </remarks>
    [Fact]
    public void EveryUiTextAssertion_QuotesCopyTheAppActuallyShips()
    {
        var uiTestsDir = Path.Combine(FindRepoRoot(), "SysManager", "SysManager.UITests");
        Assert.True(Directory.Exists(uiTestsDir),
            $"UI test project not found at {uiTestsDir} — the guard would pass vacuously");

        // Everything the app can render: XAML markup plus C# status/message strings.
        var appDir = FindAppProjectDir();
        var rendered = Directory
            .EnumerateFiles(appDir, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                       StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                       StringComparison.Ordinal))
            .Select(f => Collapse(File.ReadAllText(f)))
            .ToArray();

        Assert.True(rendered.Length >= 100,
            $"only {rendered.Length} app source files read — the guard is not seeing the app it thinks it is");

        var offenders = new List<string>();
        var assertionsChecked = 0;

        foreach (var file in Directory.GetFiles(uiTestsDir, "*.cs"))
        {
            if (Path.GetFileName(file) == "AppFixture.cs") continue; // the helper layer, not an assertion

            var lines = File.ReadAllLines(file);

            // Which locals actually reach a text-wait call? Only those carry app copy. Collected first,
            // because the assignment appears BEFORE the call that consumes it.
            var waitedLocals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines.Where(IsCode))
                foreach (var use in TextWaitOnLocal().Matches(line).Cast<Match>())
                    waitedLocals.Add(use.Groups["name"].Value);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!IsCode(lines[i])) continue;

                // Read a whole STATEMENT, not a line: `var expected = cond ? "a" : "b";` puts each
                // branch on its own line, so the assignment line carries no literal at all.
                var statement = lines[i];
                var span = 1;
                while (!statement.TrimEnd().EndsWith(';') && i + span < lines.Length && span <= 6)
                {
                    if (IsCode(lines[i + span])) statement += " " + lines[i + span].Trim();
                    span++;
                }

                foreach (var literal in AssertedTextLiterals(statement, waitedLocals)
                             .Where(IsUserFacingSentence))
                {
                    assertionsChecked++;
                    var needle = Collapse(literal);
                    if (!rendered.Any(body => body.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}  \"{literal}\"");
                }
            }
        }

        // Vacuity floor: if the shape detection breaks, the loop above checks nothing and reports
        // success — the exact failure mode that let a permanently-red UI assertion survive weeks of
        // green-looking runs. The floor is the ENUMERATED population at the time of writing (8 phrase
        // literals across the UI tests, including both Uninstaller branches), not a number picked to make
        // the assertion pass: it was set after listing them, and it caught the first two attempts at this
        // guard, whose narrower shape detection saw only 3 and then 8-minus-the-ternary.
        Assert.True(assertionsChecked >= 7,
            $"only {assertionsChecked} UI text assertions parsed — the guard is measuring nothing, fix it "
            + "rather than trusting its pass");

        Assert.True(offenders.Count == 0,
            "these UI tests wait for wording the app does not ship anywhere, so they can never pass — "
            + "the copy was almost certainly reworded without updating the assertion. Quote a stable "
            + "FRAGMENT of the current text instead of a whole sentence:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>Whitespace-collapsed and trimmed, so XAML attribute wrapping cannot hide a match.</summary>
    private static string Collapse(string text) => WhitespaceRun().Replace(text, " ").Trim();

    /// <summary>Code, not a comment — a comment's prose is never an assertion.</summary>
    /// <remarks>
    /// Its own explanatory text is the classic trap: two earlier guards in this file reported the wrong
    /// colour because they matched the comment describing them rather than the construct.
    /// </remarks>
    private static bool IsCode(string line)
    {
        var trimmed = line.TrimStart();
        return !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith('*');
    }

    /// <summary>
    /// The literals on this line that the app is expected to RENDER: arguments passed straight to a
    /// text-waiting helper, plus the branches of a local that is later handed to one.
    /// </summary>
    /// <remarks>
    /// <para>Matching the ARGUMENT rather than the whole line keeps an assertion-failure message out of
    /// scope even when it shares a line with the call, as in
    /// <c>Assert.True(_fx.HasText("Dashboard"), "Dashboard did not recover…")</c> — only the first
    /// literal is app copy.</para>
    /// <para>The local matters because the original defect built its text in a ternary and passed the
    /// VARIABLE, so a call-site-only regex reported zero findings on a tree that genuinely had one. But
    /// the local must be one that REACHES a text-wait call: an earlier version of this guard keyed on the
    /// `? "…" : "…"` shape alone and immediately flagged a ternary building an assertion-failure MESSAGE
    /// — same syntax, opposite meaning. Resolving the name is the difference between checking what the
    /// app must render and checking prose addressed to whoever reads the failure.</para>
    /// </remarks>
    private static IEnumerable<string> AssertedTextLiterals(string line, HashSet<string> waitedLocals)
    {
        foreach (var call in TextWaitCall().Matches(line).Cast<Match>())
            yield return call.Groups["text"].Value;

        // `var expected = cond ? "a" : "b";` spreads the branches over following lines, so track the
        // assignment and keep reading while the statement continues.
        var assignment = CopyLocalAssignment().Match(line);
        if (!assignment.Success || !waitedLocals.Contains(assignment.Groups["name"].Value)) yield break;

        foreach (var literal in QuotedLiteral().Matches(line).Cast<Match>())
            yield return literal.Groups["text"].Value;
    }

    /// <summary>
    /// A literal worth checking is a multi-word PHRASE — three words or more. That is the shape a copy
    /// edit breaks. Short waits ("CPU", "drivers found") are deliberately durable fragments and stay out
    /// of scope, as do literals carrying markup, path, or format-hole characters (ids, xpaths, templates).
    /// </summary>
    private static bool IsUserFacingSentence(string text) =>
        text.Count(c => c == ' ') >= 2
        && text.IndexOfAny(['\\', '/', '{', '}', '<', '>']) < 0;

    [GeneratedRegex("\"(?<text>[^\"]*)\"", RegexOptions.Compiled)]
    private static partial Regex QuotedLiteral();

    /// <summary>A literal passed directly to one of the fixture's text-waiting helpers.</summary>
    [GeneratedRegex(@"(?:HasText|HasTextInCurrentTab|WaitForText|WaitForTextInCurrentTab)\(\s*""(?<text>[^""]*)""",
                    RegexOptions.Compiled)]
    private static partial Regex TextWaitCall();

    /// <summary>A local being assigned — its name is checked against the ones that reach a text wait.</summary>
    [GeneratedRegex(@"\bvar\s+(?<name>\w+)\s*=", RegexOptions.Compiled)]
    private static partial Regex CopyLocalAssignment();

    /// <summary>A local (not a literal) handed to a text-waiting helper — that is what makes it copy.</summary>
    [GeneratedRegex(@"(?:HasText|HasTextInCurrentTab|WaitForText|WaitForTextInCurrentTab)\(\s*(?<name>[A-Za-z_]\w*)\s*[,)]",
                    RegexOptions.Compiled)]
    private static partial Regex TextWaitOnLocal();

    // WhitespaceRun() is declared once, near the XAML attribute readers that first needed it — the same
    // collapse rule serves both, so it is not redeclared here.

    /// <summary>
    /// Every ETA text property must either be cleared when its operation ends, or live inside a section
    /// the view hides when the operation is not running. Otherwise the last value stays on screen: Speed
    /// Test left the literal word "done" under BOTH its cards — they share one property — until the next
    /// run, and after a cancel it stranded whatever the last tick produced, typically "a few seconds".
    /// <para>Two accepted shapes, because both are already in use and both are correct: clear it in
    /// <c>finally</c> (AppUpdates, BulkInstaller, Uninstaller, Cleanup, and now SpeedTest), or gate the
    /// containing panel on an <c>Is…ing</c> flag (DeepCleanup). What is NOT accepted is neither.</para>
    /// <para>Matched on the CLEARING STATEMENT, not on the words of this comment: a guard that keys on
    /// prose passes because of its own explanation. The population is enumerated from the view models that
    /// actually own an ETA property, so adding a seventh consumer without clearing it fails here.</para>
    /// </summary>
    [Fact]
    public void EveryEtaTextProperty_IsClearedWhenItsOperationEnds()
    {
        var appDir = FindAppProjectDir();
        var vmDir = Path.Combine(appDir, "ViewModels");
        var viewsDir = Path.Combine(appDir, "Views");

        var offenders = new List<string>();
        var checkedProperties = 0;

        foreach (var file in Directory.GetFiles(vmDir, "*ViewModel.cs"))
        {
            var source = File.ReadAllText(file);
            var vmName = Path.GetFileNameWithoutExtension(file);

            foreach (var property in EtaTextProperties(source))
            {
                checkedProperties++;

                // Shape 1: EVERY try/finally that feeds this property also clears it, so a run ending any
                // way at all — success, error, cancel — leaves nothing behind.
                //
                // Counted per block, not "somewhere in the file": an `Any` over the whole source let one
                // operation drop its clear while a sibling supplied the match. Speed Test has two, Ookla
                // and HTTP, sharing one property — exactly the shape in which a half-fix reads as done.
                var feedingBlocks = TryFinallyBlocksFeeding(source, property);
                if (feedingBlocks.Count > 0 && feedingBlocks.All(b => ClearsProperty(b, property)))
                    continue;

                // Shape 2: the ETA element sits inside a container the view hides while the operation is
                // not running, so a stale value can never be seen.
                //
                // Scoped to the ETA element's OWN ancestor chain, not to "this file mentions an Is…ing
                // binding somewhere". The looser form waved SpeedTestView through on the strength of the
                // Visibility bindings on its ProgressBar and Cancel button, which have nothing to do with
                // the ETA TextBlock — leaving this guard green against the exact defect it was written for.
                var viewPath = Path.Combine(viewsDir, vmName.Replace("ViewModel", "View") + ".xaml");
                if (File.Exists(viewPath) && EtaElementSitsInAFlagGatedContainer(viewPath, property))
                    continue;

                offenders.Add($"{vmName}.{property}");
            }
        }

        // Vacuity floor from an enumerated population: AppUpdates, BulkInstaller, Cleanup (×2),
        // DeepCleanup (×2), SpeedTest, Uninstaller — eight ETA properties across six view models. A
        // regex that silently stopped matching would otherwise make this pass by checking nothing.
        Assert.True(checkedProperties >= 8,
            $"only {checkedProperties} ETA properties were found — EtaBackingField() has stopped "
          + "matching, so this guard is measuring nothing.");

        Assert.True(offenders.Count == 0,
            "these ETA texts are neither cleared in a finally nor hidden with their section, so the last "
          + "value stays on screen after the operation ends: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Names of the ETA text properties a view model owns. Matches the <c>[ObservableProperty]</c> backing
    /// field, whose generated property is what the view binds.
    /// </summary>
    private static IEnumerable<string> EtaTextProperties(string source)
    {
        foreach (Match m in EtaBackingField().Matches(source))
        {
            var field = m.Groups["name"].Value;   // _upgradeEtaText
            yield return char.ToUpperInvariant(field[1]) + field[2..];
        }
    }

    /// <summary>
    /// True when the element displaying <paramref name="property"/> has an ANCESTOR whose
    /// <c>Visibility</c> binds to an <c>Is…ing</c> flag — so the whole section disappears when the
    /// operation is not running and a stale value is unreachable.
    /// <para>Walks the real XAML tree rather than searching the file, and deliberately ignores a
    /// <c>Visibility</c> on the ETA element itself: binding an element's visibility to the very string it
    /// displays is not a gate, it is what kept the stale text on screen.</para>
    /// </summary>
    private static bool EtaElementSitsInAFlagGatedContainer(string viewPath, string property)
    {
        var root = System.Xml.Linq.XDocument.Load(viewPath).Root;
        if (root is null) return false;

        var binding = $"{{Binding {property}}}";
        foreach (var element in root.Descendants())
        {
            if ((string?)element.Attribute("Text") != binding) continue;

            for (var ancestor = element.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                var visibility = (string?)ancestor.Attribute("Visibility");
                if (visibility is not null && Regex.IsMatch(visibility, @"^\{Binding\s+Is\w+ing\b"))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The <c>finally</c> body of every method that ASSIGNS <paramref name="property"/> — i.e. every
    /// operation that can leave a value on screen. A method that assigns it but has no <c>finally</c>
    /// yields an empty string, which fails <see cref="ClearsProperty"/> and is reported.
    /// </summary>
    private static List<string> TryFinallyBlocksFeeding(string source, string property)
    {
        var bodies = new List<string>();
        foreach (var method in MethodBodies(source))
        {
            // An assignment FROM the ETA calculator is what makes this method a feeder; the clear itself
            // (`= string.Empty`) must not count, or a method that only resets it would look like one.
            if (!Regex.IsMatch(method, Regex.Escape(property) + @"\s*=\s*(?!string\.Empty|"""")"))
                continue;

            var finallyBodies = FinallyBodies(method).ToList();
            bodies.Add(finallyBodies.Count > 0 ? string.Join('\n', finallyBodies) : string.Empty);
        }
        return bodies;
    }

    /// <summary>True when the block assigns the property an empty string.</summary>
    private static bool ClearsProperty(string block, string property) =>
        block.Contains($"{property} = string.Empty", StringComparison.Ordinal)
        || block.Contains($"{property} = \"\"", StringComparison.Ordinal);

    /// <summary>
    /// Each method body in a source file, brace-balanced from its opening <c>{</c>. Coarse by design: it
    /// only has to separate one operation's try/finally from another's.
    /// </summary>
    private static IEnumerable<string> MethodBodies(string source)
    {
        foreach (Match m in MethodSignature().Matches(source))
        {
            var open = source.IndexOf('{', m.Index + m.Length - 1);
            if (open < 0) continue;

            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                {
                    yield return source[(open + 1)..i];
                    break;
                }
            }
        }
    }

    /// <summary>A method declaration line — the anchor from which a body is brace-matched.</summary>
    [GeneratedRegex(@"^\s{4}(?:\[[^\]]+\]\s*)?(?:private|internal|public|protected)[^;=\r\n]*\([^;)]*\)\s*$",
                    RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex MethodSignature();

    /// <summary>
    /// The body of each <c>finally</c> block in a source file. Brace-balanced rather than regex-matched,
    /// because a finally body contains braces of its own.
    /// </summary>
    private static IEnumerable<string> FinallyBodies(string source)
    {
        var at = 0;
        while (true)
        {
            var keyword = source.IndexOf("finally", at, StringComparison.Ordinal);
            if (keyword < 0) break;
            at = keyword + "finally".Length;

            var open = source.IndexOf('{', keyword);
            if (open < 0) break;

            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                {
                    yield return source[(open + 1)..i];
                    at = i;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// An <c>[ObservableProperty]</c> field holding ETA text — the thing that must not go stale. Both
    /// spellings in use are matched: seven fields are named <c>…EtaText</c>, and Speed Test's is
    /// <c>_estimatedTime</c>. Keying on "eta" alone missed exactly the one that carried the defect.
    /// </summary>
    [GeneratedRegex(@"\[ObservableProperty\]\s*private\s+string\s+(?<name>_\w*(?:[Ee]ta|[Ee]stimated)\w*)\s*=",
                    RegexOptions.Compiled)]
    private static partial Regex EtaBackingField();

    /// <summary>
    /// Every write on <c>IAudioMixerService</c> that reports whether it was applied must have that answer
    /// CONSULTED at each call site. All three returned <c>bool</c>, documented "Returns true if the change
    /// was applied", and all three results were discarded — so a refused write left the slider sitting at
    /// the new value while the app kept playing at the old one, silently.
    /// <para>Phrased over the INTERFACE, not over the call sites: the population is discovered from
    /// <c>IAudioMixerService</c>'s bool-returning members, so adding a fourth write and ignoring it fails
    /// here. The satisfiable-one-at-a-time form ("this call site must check") would not have caught the
    /// original defect either, because no call site checked.</para>
    /// <para>Consulted means the result reaches a condition or a variable — <c>if (!x.Set…)</c>,
    /// <c>var ok = x.Set…</c>, <c>return x.Set…</c>. A bare statement call is the defect.</para>
    /// </summary>
    [Fact]
    public void EveryAudioWriteThatReportsSuccess_HasThatAnswerConsulted()
    {
        var appDir = FindAppProjectDir();

        var contract = File.ReadAllText(Path.Combine(appDir, "Services", "IAudioMixerService.cs"));
        var writes = BoolReturningMember().Matches(contract).Select(m => m.Groups["name"].Value).ToList();

        // Vacuity floor from an enumerated population: SetVolume, SetMute, SetSessionOutputDevice.
        Assert.True(writes.Count >= 3,
            $"only {writes.Count} bool-returning writes found on IAudioMixerService — the member regex has "
          + "stopped matching, so this guard is measuring nothing.");

        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(appDir, "ViewModels"), "*.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i].Trim();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;   // never match our own prose

                foreach (var write in writes)
                {
                    if (!code.Contains($".{write}(", StringComparison.Ordinal)) continue;

                    // A bare statement call: the line IS the invocation and nothing receives the answer.
                    var bare = code.StartsWith("_service.", StringComparison.Ordinal)
                            && code.EndsWith(");", StringComparison.Ordinal)
                            && !code.Contains('=', StringComparison.Ordinal);
                    if (bare)
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1} — {write} result discarded");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These audio writes report whether they were applied and the answer is thrown away, so a "
          + "refused change leaves the control showing a value the system never took, with nothing said "
          + "to the user:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>A bool-returning member on an interface — a write that reports its own outcome.</summary>
    [GeneratedRegex(@"^\s+bool\s+(?<name>\w+)\s*\(", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex BoolReturningMember();

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
