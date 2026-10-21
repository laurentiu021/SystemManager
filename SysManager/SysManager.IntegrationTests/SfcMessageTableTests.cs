// SysManager · SfcMessageTableTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// The sentences SFC ends a scan with, read from this Windows' own <c>sfc.exe</c>, and SFC's output, which decodes
/// with the encoding System Fixes starts it with (#2835).
/// </summary>
/// <remarks>
/// Nothing here scans or repairs: the table is read from the file as data, and <c>sfc /?</c> only prints its help.
/// </remarks>
public class SfcMessageTableTests
{
    private static string Sfc()
    {
        var path = SystemPaths.ResolveSystemTool("sfc.exe");
        if (!File.Exists(path)) Assert.Skip("sfc.exe is not present on this host.");
        return path;
    }

    [Fact]
    public void TheMessageTable_HasASentenceForEveryOutcome_InWindowsLanguage()
    {
        var table = SfcOutput.ReadTable(Sfc(), language: 0);

        Assert.Equal(Enum.GetValues<SfcOutcome>(), table.Select(t => t.Outcome));
        Assert.All(table, t => Assert.False(string.IsNullOrWhiteSpace(t.FirstLine)));
        Assert.Equal(table.Count, table.Select(t => t.FirstLine).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheUsEnglishTable_IsTheEnglishKeptInCode()
    {
        // The English in code is what a table that cannot be read falls back to, so it is Windows' own, word for word.
        var table = SfcOutput.ReadTable(Sfc(), language: 0x0409);
        if (table.Count == 0) Assert.Skip("This Windows has no US English message table for sfc.exe.");

        Assert.Equal(SfcOutput.Messages.Select(m => (m.Outcome, m.English)), table);
    }

    [Fact]
    public async Task SfcsOutput_ReadsAsText_InTheEncodingTheTabStartsItWith()
    {
        Sfc();

        var lines = await LinesOfHelp(SfcOutput.Encoding);

        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, l => l.Contains('\0'));
        Assert.Contains(lines, l => l.Contains("Microsoft", StringComparison.Ordinal));

        // The control: read as the OEM code page, as the tab read it before, the same help has a NUL between letters,
        // so the check above is not passing over text that never had them.
        Assert.Contains(await LinesOfHelp(PowerShellRunner.OemEncoding), l => l.Contains('\0'));
    }

    private static async Task<List<string>> LinesOfHelp(System.Text.Encoding encoding)
    {
        var runner = new PowerShellRunner();
        List<string> lines = [];
        runner.LineReceived += line =>
        {
            lock (lines) lines.Add(line.Text);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await runner.RunProcessAsync("sfc.exe", "/?", timeout.Token, encoding);

        lock (lines) return [.. lines];
    }
}
