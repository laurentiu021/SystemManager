// SysManager · WorkflowJobs
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.RegularExpressions;

namespace SysManager.Tests;

/// <summary>
/// The jobs of a GitHub Actions workflow, read from its lines, for the guards that hold the release pipeline to what
/// its documents say.
/// </summary>
/// <remarks>
/// A job runs when its <c>needs</c> let it, not where it sits in the file, so a guard about what runs after what has to
/// know which job a line belongs to. Shared because the architecture and the security guards both read the winget job,
/// and two slicers could disagree about where it ends.
/// </remarks>
internal static partial class WorkflowJobs
{
    /// <summary>The indexes of the lines that start a job, under <c>jobs:</c>, in file order.</summary>
    internal static List<int> Keys(string[] lines)
    {
        var jobs = Array.FindIndex(lines, l => l.TrimEnd() == "jobs:");
        Assert.True(jobs >= 0, "the workflow has no jobs: line, so none of its jobs can be read.");
        return Enumerable.Range(jobs + 1, lines.Length - jobs - 1).Where(i => JobKey().IsMatch(lines[i])).ToList();
    }

    /// <summary>
    /// The job that holds the given line, from its key to the next job's, as code: comment lines are left out, so
    /// prose naming a setting cannot stand in for it.
    /// </summary>
    internal static string Containing(string[] lines, int line)
    {
        var keys = Keys(lines);
        var start = keys.LastOrDefault(k => k <= line, -1);
        Assert.True(start >= 0, $"line {line + 1} is not inside any job.");
        var end = keys.FirstOrDefault(k => k > line, lines.Length);
        return string.Join('\n', lines[start..end].Where(l => !l.TrimStart().StartsWith('#')));
    }

    /// <summary>
    /// Whether the job whose key is at the given line holds a token that can write: what its own <c>permissions</c>
    /// grant, else the workflow's, and with neither the repository's default, which can.
    /// </summary>
    internal static bool CanWrite(string[] lines, int job)
    {
        var keys = Keys(lines);
        var end = keys.FirstOrDefault(k => k > job, lines.Length);
        return Grants(lines[job..end], "    ") ?? Grants(lines[..keys[0]], "") ?? true;
    }

    /// <summary>Whether the job whose key is at the given line restores, builds or runs NuGet packages' code.</summary>
    internal static bool BuildsPackages(string[] lines, int job)
        => Containing(lines, job).Split('\n').Any(l => PackageCommand().IsMatch(l));

    /// <summary>A permissions block at an indent: whether it grants a write, or null when there is none.</summary>
    private static bool? Grants(string[] lines, string indent)
    {
        var key = $"{indent}permissions:";
        var at = Array.FindIndex(lines, l => l.StartsWith(key, StringComparison.Ordinal));
        if (at < 0) return null;
        var inline = lines[at][key.Length..].Split('#')[0].Trim();
        if (inline.Length > 0) return inline == "write-all" || inline.Contains(": write", StringComparison.Ordinal);
        return lines[(at + 1)..]
            .Where(l => !l.TrimStart().StartsWith('#'))
            .TakeWhile(l => l.StartsWith($"{indent}  ", StringComparison.Ordinal))
            .Any(l => l.Split('#')[0].TrimEnd().EndsWith(": write", StringComparison.Ordinal));
    }

    /// <summary>A command that restores, builds or runs packages' code: dotnet's, or the publish script.</summary>
    [GeneratedRegex(@"\bdotnet\s+(?:restore|build|test|publish|run|pack|tool|format)\b|publish\.ps1",
        RegexOptions.CultureInvariant)]
    private static partial Regex PackageCommand();

    /// <summary>A job's key: two spaces of indent, a name and a colon, and nothing after it but a comment.</summary>
    [GeneratedRegex(@"^  [A-Za-z0-9_-]+:\s*(#.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex JobKey();
}
