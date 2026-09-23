// SysManager · TestPaths — one place that knows where the repository is
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;

namespace SysManager.Tests;

/// <summary>
/// Resolves the repository directories a source-reading test needs, by walking up from the test output.
/// </summary>
/// <remarks>
/// The build copies no <c>.cs</c> or <c>.xaml</c> into the test output, so a guard that reads source has to
/// find the tree first. That walk was written 31 times across 25 files in this project, behind 32 private
/// helpers with 17 different names, and the copies had already drifted in ways that matter:
/// <list type="bullet">
/// <item>The repository root had two definitions — <c>SUPPORT.md</c> with <c>README.md</c> in two files, the
/// <c>.github/workflows</c> folder in a third. They agree today because all three sit in the same directory,
/// which is exactly the kind of agreement that stops holding without anyone noticing.</item>
/// <item>The app project was located by six different markers: its own <c>.csproj</c> one or two levels down,
/// <c>MainWindow.xaml</c>, or the presence of its <c>Services</c>, <c>Views</c>, <c>ViewModels</c> or
/// <c>Helpers</c> folder. Four of those verify a NEIGHBOUR of the directory they return instead of the
/// directory itself, so they answer confidently for a tree that does not contain the project.</item>
/// <item>The test project's own walk fails when the suite is hosted from somewhere other than its output
/// folder, and <c>ArchitectureTests</c> grew the sibling fallback below to cover it — while
/// <c>ServicesViewModelTests</c>, holding the same walk, did not. One copy was repaired and taught the other
/// nothing, which is the whole failure mode.</item>
/// <item>Two failure idioms: throwing, or <c>Assert.NotNull(dir)</c> followed by <c>dir!</c>. The second only
/// works inside a test method, so a helper written that way cannot move anywhere useful.</item>
/// </list>
/// <para>Counting them was itself instructive: the first sweep found 28, because it keyed on
/// <c>new DirectoryInfo(</c>. Three copies wrote the same walk in shapes that needle could not see — one
/// spelled <c>new System.IO.DirectoryInfo</c> fully qualified, one walked plain strings through
/// <c>Path.GetDirectoryName</c> and never named <c>DirectoryInfo</c> at all, and one held no walk because it
/// delegated to a sibling helper. That is why the guard below looks for <c>AppContext.BaseDirectory</c>, the
/// one thing all of them had to name.</para>
/// <para>Every resolver here verifies the thing it returns rather than something beside it, and throws with
/// the starting directory in the message. <see cref="ArchitectureTests.EveryRepositoryPathATestNeeds_IsAskedForInOnePlace"/>
/// keeps the walk here — copy 32 would restore the drift in silence, since a walk that lands in the wrong
/// place still yields a confident verdict. See <c>SourceBraces</c> and <c>Symlinks</c> for the same shape.</para>
/// </remarks>
internal static class TestPaths
{
    /// <summary>The repository root — the docs and workflows the guards read are not copied to the output.</summary>
    internal static string RepoRoot() => Ancestor(
        dir => File.Exists(Path.Combine(dir, "SUPPORT.md")) && File.Exists(Path.Combine(dir, "README.md")),
        "the repository root");

    /// <summary>The solution folder, <c>&lt;repo&gt;/SysManager</c>, which holds every project.</summary>
    internal static string SolutionDir() => Directory.GetParent(AppProject())!.FullName;

    /// <summary>The app project directory, <c>&lt;repo&gt;/SysManager/SysManager</c>.</summary>
    /// <remarks>
    /// Keyed on the project file rather than on one of its folders, so what it returns is the project it
    /// claims to have found.
    /// </remarks>
    internal static string AppProject() => Path.Combine(
        Ancestor(dir => File.Exists(Path.Combine(dir, "SysManager", "SysManager.csproj")),
                 "the SysManager app project"),
        "SysManager");

    /// <summary>The source directory of this test project.</summary>
    /// <remarks>
    /// Walks up from the output folder first, which is how it resolves under <c>dotnet test</c>. That walk
    /// fails whenever the tests are hosted from elsewhere in the tree, and then every guard reading test
    /// source throws instead of running. The fallback covers it: the app project is found by a walk looking
    /// for a SUBPATH, which succeeds from anywhere under the repository, and this project is its sibling.
    /// </remarks>
    internal static string TestProject()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SysManager.Tests.csproj"))) return dir.FullName;
        }

        var sibling = Path.Combine(SolutionDir(), "SysManager.Tests");
        if (File.Exists(Path.Combine(sibling, "SysManager.Tests.csproj"))) return sibling;

        throw new DirectoryNotFoundException(
            "Could not locate the SysManager.Tests source directory from " + AppContext.BaseDirectory);
    }

    /// <summary>A file inside the app project, by the path segments below it.</summary>
    /// <param name="parts">The segments below the app project, for example <c>"Views", "AboutView.xaml"</c>.</param>
    /// <exception cref="FileNotFoundException">The file is not there, so the caller would assert over nothing.</exception>
    internal static string AppFile(params string[] parts)
    {
        var path = Path.Combine([AppProject(), .. parts]);
        if (!File.Exists(path)) throw new FileNotFoundException($"Not found in the app project: {path}", path);

        return path;
    }

    /// <summary>A directory inside the app project, by the path segments below it.</summary>
    /// <param name="parts">The segments below the app project, for example <c>"ViewModels"</c>.</param>
    /// <exception cref="DirectoryNotFoundException">The directory is not there, so an enumeration of it would be empty.</exception>
    internal static string AppDir(params string[] parts)
    {
        var path = Path.Combine([AppProject(), .. parts]);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Not found in the app project: {path}");

        return path;
    }

    /// <summary>The nearest ancestor of the test output that <paramref name="found"/> accepts.</summary>
    /// <param name="found">Whether a candidate directory is the one being looked for.</param>
    /// <param name="what">What is being looked for, for the exception message.</param>
    private static string Ancestor(Func<string, bool> found, string what)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (found(dir.FullName)) return dir.FullName;
        }

        throw new DirectoryNotFoundException($"Could not locate {what} from " + AppContext.BaseDirectory);
    }
}
