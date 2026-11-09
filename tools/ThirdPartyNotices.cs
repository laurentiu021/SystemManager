// SysManager · ThirdPartyNotices
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

// Writes THIRD-PARTY-NOTICES.txt from SysManager's restore graph: the license and notices of each package whose code
// the exe carries, and of the .NET runtime it is published with (#2856). From the repository root:
//
//   dotnet run tools/ThirdPartyNotices.cs
//
// It restores the app first as the release publishes it, for win-x64 and self-contained, so the runtime packs are in the
// graph. Run it after a dependency changes and commit what it writes; the release runs it again before publishing, so
// the exe carries the notices of exactly what it was built from. It stops, writing nothing, at a package whose license
// it has no text for: give that package an entry in tools/licenses/packages.json.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

const string Repository = "https://github.com/laurentiu021/SystemManager";
const string Rule = "================================================================================";

var root = Directory.GetCurrentDirectory();
var project = Path.Combine(root, "SysManager", "SysManager", "SysManager.csproj");
var kept = Path.Combine(root, "tools", "licenses");
var output = Path.Combine(root, "THIRD-PARTY-NOTICES.txt");
if (!File.Exists(project) || !Directory.Exists(kept))
    return Fail($"Run this from the repository root: {project} or {kept} is not there.");

// Restored as the release publishes the exe: the runtime packs a self-contained build carries are only in that graph.
var restore = new ProcessStartInfo("dotnet") { UseShellExecute = false };
foreach (var arg in new[] { "restore", project, "-r", "win-x64", "-p:SelfContained=true", "--nologo", "-v", "q" })
    restore.ArgumentList.Add(arg);
using (var process = Process.Start(restore) ?? throw new InvalidOperationException("dotnet did not start"))
{
    process.WaitForExit();
    if (process.ExitCode != 0) return Fail($"dotnet restore exited with {process.ExitCode}.");
}

using var assets = JsonDocument.Parse(File.ReadAllText(
    Path.Combine(root, "SysManager", "SysManager", "obj", "project.assets.json")));
var packageFolder = assets.RootElement.GetProperty("packageFolders").EnumerateObject().First().Name;
var targets = assets.RootElement.GetProperty("targets").EnumerateObject().ToList();
var framework = targets.Single(t => !t.Name.Contains('/')).Name;
var graph = targets.Single(t => t.Name == framework).Value;

using var packagesJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(kept, "packages.json")));
var overrides = packagesJson.RootElement.EnumerateObject()
    .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

var components = new List<Component>();
var problems = new List<string>();
foreach (var entry in graph.EnumerateObject().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
{
    if (entry.Value.GetProperty("type").GetString() != "package" || !Ships(entry.Value)) continue;
    var slash = entry.Name.IndexOf('/', StringComparison.Ordinal);
    var (id, version) = (entry.Name[..slash], entry.Name[(slash + 1)..]);
    var folder = Path.Combine(packageFolder, id.ToLowerInvariant(), version.ToLowerInvariant());
    var nuspec = Nuspec.Read(Path.Combine(folder, id.ToLowerInvariant() + ".nuspec"));
    JsonElement? over = overrides.TryGetValue(id, out var found) ? found : null;

    var license = nuspec.LicenseType == "expression" ? nuspec.License : Text(over, "license");
    if (license is null)
    {
        problems.Add($"{id}: its license is a {nuspec.LicenseType ?? "URL"} ({nuspec.License ?? nuspec.LicenseUrl ?? "none"}); " +
            "give it a \"license\" in tools/licenses/packages.json");
        continue;
    }

    // The license text, the most specific first: a copy kept here of the file a URL-only license points at; the
    // package's own license file, which carries its copyright line; or, for MIT, the standard text with the package's
    // copyright notice. Otherwise the section refers to the standard text under LICENSE TEXTS.
    string? text = Text(over, "text") is { } copy ? File.ReadAllText(Path.Combine(kept, copy)) : null;
    if (text is null && nuspec.LicenseType == "file" && nuspec.License is { } file)
        text = File.Exists(Path.Combine(folder, file)) ? File.ReadAllText(Path.Combine(folder, file)) : null;
    text ??= FirstFile(folder, "LICENSE", "LICENSE.txt", "LICENSE.md");
    if (text is null && license == "MIT") text = Mit(kept, nuspec);
    if (text is null && !Licenses.Files.ContainsKey(license))
    {
        problems.Add($"{id}: no text for {license}; keep one under tools/licenses and name it in packages.json");
        continue;
    }

    List<string> notices = [];
    if (FirstFile(folder, "NOTICE", "NOTICE.txt", "NOTICE.md") is { } notice) notices.Add(notice);
    if (FirstFile(folder, "THIRD-PARTY-NOTICES.txt", "ThirdPartyNotices.txt") is { } third) notices.Add(third);

    components.Add(new Component(id, license, nuspec.Copyright, nuspec.ProjectUrl ?? nuspec.Repository,
        Text(over, "source") ?? nuspec.Repository ?? nuspec.ProjectUrl, text, notices));
}

// The runtime packs a self-contained publish copies into the exe, from the same restore: those of the frameworks the app
// references. The restore also fetches ASP.NET Core's, which the app does not use.
List<Component> runtimes = [];
var restored = assets.RootElement.GetProperty("project").GetProperty("frameworks").GetProperty(framework);
var references = restored.GetProperty("frameworkReferences").EnumerateObject().Select(r => r.Name).ToList();
if (restored.TryGetProperty("downloadDependencies", out var packs))
{
    foreach (var pack in packs.EnumerateArray().OrderBy(p => p.GetProperty("name").GetString(), StringComparer.Ordinal))
    {
        const string Suffix = ".Runtime.win-x64";
        var name = pack.GetProperty("name").GetString()!;
        if (!name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) continue;
        var referenced = name[..^Suffix.Length];
        if (!references.Any(r => r == referenced || r.StartsWith(referenced + ".", StringComparison.Ordinal))) continue;
        var version = pack.GetProperty("version").GetString()!.Trim('[', ']').Split(',')[0].Trim();
        var folder = Path.Combine(packageFolder, name.ToLowerInvariant(), version);
        var (title, projectUrl) = name[..^Suffix.Length] switch
        {
            "Microsoft.NETCore.App" => (".NET runtime (Microsoft.NETCore.App)", "https://github.com/dotnet/runtime"),
            "Microsoft.WindowsDesktop.App" => ("Windows Desktop runtime (Microsoft.WindowsDesktop.App)",
                "https://github.com/dotnet/wpf and https://github.com/dotnet/winforms"),
            var other => (other, "https://github.com/dotnet"),
        };
        if (FirstFile(folder, "LICENSE.TXT", "LICENSE") is not { } licenseText)
        {
            problems.Add($"{name} {version}: no LICENSE in {folder}");
            continue;
        }
        List<string> notices = FirstFile(folder, "THIRD-PARTY-NOTICES.TXT") is { } notice ? [notice] : [];
        runtimes.Add(new Component(title, "MIT", null, projectUrl, null, licenseText, notices));
    }
}
if (runtimes.Count == 0) problems.Add("no runtime pack in the restore graph: was the app restored self-contained?");

// The standard texts the components refer to: each license a component names without carrying its own copy, and the
// GNU GPL, which the GNU LGPL builds on.
var appendix = components.Where(c => c.Text is null).Select(c => c.License)
    .Concat(components.Any(c => c.License.StartsWith("LGPL", StringComparison.Ordinal)) ? new[] { "GPL-3.0-only" } : [])
    .Distinct().Order(StringComparer.Ordinal).ToList();
foreach (var id in appendix.Where(id => !Licenses.Files.ContainsKey(id)))
    problems.Add($"no standard text kept for {id}");

if (problems.Count > 0)
{
    Console.Error.WriteLine("THIRD-PARTY-NOTICES.txt was not written:");
    foreach (var problem in problems) Console.Error.WriteLine("  " + problem);
    return 1;
}

// One section for each set of packages that share a license, its text, their copyright, their project and their notices.
var sections = components
    .GroupBy(c => (c.License, c.Copyright, c.Project, Hash(c.Text), string.Join(',', c.Notices.Select(Hash))))
    .Select(g => g.ToList())
    .OrderBy(g => g[0].Name, StringComparer.OrdinalIgnoreCase)
    .Concat(runtimes.Select(r => new List<Component> { r }))
    .ToList();

var lgpl = components.Where(c => c.License.StartsWith("LGPL", StringComparison.Ordinal)).Select(c => c.Name).ToList();
var sb = new StringBuilder();
sb.AppendLine("THIRD-PARTY SOFTWARE NOTICES");
sb.AppendLine("============================");
sb.AppendLine();
Paragraph(sb, "SysManager is released under the MIT License, in the LICENSE file beside its source. Its exe also " +
    "carries the open-source components listed below, each under its own license, and the .NET runtime it is " +
    "published with. Their licenses and notices follow, then the full text of each license that a component names " +
    "without carrying its own copy.");
Paragraph(sb, $"SysManager's own source is at {Repository}, with what it takes to build the exe from it, in the " +
    "README under \"Build from source\"." + (lgpl.Count == 0 ? "" :
        " That is also how to build it with a modified version of " + string.Join(" or ", lgpl) +
        ", which the GNU Lesser General Public License lets you do."));
Paragraph(sb, "The software bill of materials published with each release lists the exact version of each " +
    "component. This file is generated from SysManager's restore graph by tools/ThirdPartyNotices.cs.");
sb.AppendLine("COMPONENTS");
sb.AppendLine("----------");
foreach (var c in components.Concat(runtimes)) sb.AppendLine($"{c.Name}: {c.License}");

Dictionary<string, string> printed = [];
foreach (var section in sections) Section(sb, section, printed);

if (appendix.Count > 0)
{
    sb.AppendLine();
    sb.AppendLine();
    sb.AppendLine(Rule);
    sb.AppendLine("LICENSE TEXTS");
    sb.AppendLine(Rule);
    foreach (var id in appendix)
    {
        sb.AppendLine();
        sb.AppendLine($"--- {Licenses.Title(id)} ({id}) ---");
        sb.AppendLine();
        sb.AppendLine(Normalised(File.ReadAllText(Path.Combine(kept, Licenses.Files[id]))));
    }
}

var written = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
var current = File.Exists(output) && File.ReadAllText(output) == written;
File.WriteAllText(output, written, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine($"THIRD-PARTY-NOTICES.txt {(current ? "is current" : "written")}: {components.Count} packages in " +
    $"{sections.Count - runtimes.Count} sections, and {runtimes.Count} runtimes.");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

// A package ships when, for win-x64, it brings anything the exe loads: managed code, a native library or a resource.
static bool Ships(JsonElement package)
{
    foreach (var kind in new[] { "runtime", "native", "resource" })
    {
        if (package.TryGetProperty(kind, out var files)
            && files.EnumerateObject().Any(f => !f.Name.EndsWith("_._", StringComparison.Ordinal)))
            return true;
    }
    string[] windows = ["win-x64", "win", "any", "base"];
    return package.TryGetProperty("runtimeTargets", out var targets)
        && targets.EnumerateObject().Any(t => !t.Name.EndsWith("_._", StringComparison.Ordinal)
            && t.Value.TryGetProperty("rid", out var rid) && windows.Contains(rid.GetString()));
}

static string? Text(JsonElement? over, string property) =>
    over is { } o && o.TryGetProperty(property, out var value) ? value.GetString() : null;

static string? FirstFile(string folder, params string[] names)
{
    if (!Directory.Exists(folder)) return null;
    var files = Directory.GetFiles(folder);
    foreach (var name in names)
    {
        var match = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return File.ReadAllText(match);
    }
    return null;
}

static string Mit(string kept, Nuspec nuspec)
{
    var notice = nuspec.Copyright ?? $"The package states no copyright notice. Its authors: {nuspec.Authors}.";
    return $"MIT License\n\n{notice}\n\n{Normalised(File.ReadAllText(Path.Combine(kept, "MIT.txt")))}";
}

static string Normalised(string text) => text.Replace("\r\n", "\n").TrimEnd('\n', ' ');

static string Hash(string? text) =>
    text is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalised(text))));

static void Paragraph(StringBuilder sb, string text)
{
    var line = new StringBuilder();
    foreach (var word in text.Split(' '))
    {
        if (line.Length > 0 && line.Length + 1 + word.Length > 100)
        {
            sb.AppendLine(line.ToString());
            line.Clear();
        }
        if (line.Length > 0) line.Append(' ');
        line.Append(word);
    }
    sb.AppendLine(line.ToString());
    sb.AppendLine();
}

static void Section(StringBuilder sb, List<Component> section, Dictionary<string, string> printed)
{
    var first = section[0];
    var title = string.Join(", ", section.Select(c => c.Name));
    sb.AppendLine();
    sb.AppendLine();
    sb.AppendLine(Rule);
    sb.AppendLine(title);
    sb.AppendLine(Rule);
    sb.AppendLine($"License: {Licenses.Title(first.License)} ({first.License})" +
        (first.Text is null ? ", whose text is under LICENSE TEXTS at the end of this file." : "."));
    if (first.License.StartsWith("LGPL", StringComparison.Ordinal))
        sb.AppendLine("It builds on the GNU General Public License 3.0, whose text is under LICENSE TEXTS at the end of this file.");
    if (first.Copyright is { } copyright) sb.AppendLine($"Copyright: {copyright}");
    if (first.Project is { } project) sb.AppendLine($"Project: {project}");
    if (first.Source is { } source && (first.License.StartsWith("MPL", StringComparison.Ordinal)
                                       || first.License.StartsWith("LGPL", StringComparison.Ordinal)))
        sb.AppendLine($"Source code: {source}");
    if (first.Text is { } text)
    {
        sb.AppendLine();
        sb.AppendLine(Normalised(text));
    }
    foreach (var notice in first.Notices)
    {
        sb.AppendLine();
        sb.AppendLine("--- The notices it carries ---");
        sb.AppendLine();
        // The same file comes with more than one package: printed once, and named after that.
        if (printed.TryAdd(Hash(notice), title)) sb.AppendLine(Normalised(notice));
        else sb.AppendLine($"The same notices as those under {printed[Hash(notice)]}, above.");
    }
}

sealed record Component(string Name, string License, string? Copyright, string? Project, string? Source, string? Text,
    List<string> Notices);

sealed record Nuspec(string Authors, string? Copyright, string? LicenseType, string? License, string? LicenseUrl,
    string? ProjectUrl, string? Repository)
{
    public static Nuspec Read(string path)
    {
        var metadata = XDocument.Load(path).Root!.Elements().First(e => e.Name.LocalName == "metadata");
        XElement? Element(string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        string? Value(string name) => Element(name)?.Value.Trim() is { Length: > 0 } v ? v : null;
        var license = Element("license");
        var repository = Element("repository")?.Attribute("url")?.Value;
        return new Nuspec(Value("authors") ?? "unknown", Value("copyright"), license?.Attribute("type")?.Value,
            license?.Value.Trim(), Value("licenseUrl"), Value("projectUrl"),
            string.IsNullOrWhiteSpace(repository) ? null : repository);
    }
}

static class Licenses
{
    /// <summary>The standard texts kept under tools/licenses, for a license a component names without its own copy.</summary>
    public static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["Apache-2.0"] = "Apache-2.0.txt",
        ["MPL-2.0"] = "MPL-2.0.txt",
        ["GPL-3.0-only"] = "GPL-3.0.txt",
    };

    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["MIT"] = "MIT License",
        ["Apache-2.0"] = "Apache License 2.0",
        ["MPL-2.0"] = "Mozilla Public License 2.0",
        ["GPL-3.0-only"] = "GNU General Public License 3.0",
        ["LGPL-3.0-only"] = "GNU Lesser General Public License 3.0",
        ["LicenseRef-OpenTK"] = "The Open Toolkit library license",
    };

    /// <summary>The name a license is shown with, or its id when it has none here.</summary>
    public static string Title(string id) => Titles.TryGetValue(id, out var title) ? title : id;
}
