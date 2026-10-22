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

    /// <summary>A job's key: two spaces of indent, a name and a colon, and nothing after it but a comment.</summary>
    [GeneratedRegex(@"^  [A-Za-z0-9_-]+:\s*(#.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex JobKey();
}
