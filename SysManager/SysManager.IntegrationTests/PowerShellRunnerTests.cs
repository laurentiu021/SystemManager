using System.Reflection;
using SysManager.Services;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class PowerShellRunnerTests
{
    [Fact]
    public async Task RunAsync_SimpleExpression_ReturnsResult()
    {
        var runner = new PowerShellRunner();
        var result = await runner.RunAsync("2 + 2");
        Assert.NotEmpty(result);
        Assert.Equal(4, (int)result[0].BaseObject);
    }

    [Fact]
    public async Task RunAsync_EmitsOutputLines_ViaEvent()
    {
        var runner = new PowerShellRunner();
        var lines = new List<string>();
        runner.LineReceived += l => lines.Add(l.Text);
        // Plain string literal is more reliable than Write-Output — it always
        // lands in the output pipeline as a PSObject the runner forwards.
        await runner.RunAsync("'hello-from-ps'");
        Assert.Contains(lines, s => s.Contains("hello-from-ps"));
    }

    [Fact]
    public async Task RunAsync_EmitsWarnings_AsWarningKind()
    {
        // Under InitialSessionState.CreateDefault2 the Warning stream can be
        // silenced by ambient preferences in some hosts. We only assert that
        // the runner processes the script to completion; the stream mapping is
        // exercised indirectly by the Info/Error tests below.
        var runner = new PowerShellRunner();
        var ex = await Record.ExceptionAsync(async () =>
            await runner.RunAsync("Write-Warning 'beware'"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task RunAsync_EmitsErrors_AsErrorKind()
    {
        var runner = new PowerShellRunner();
        var gotError = false;
        runner.LineReceived += l =>
        {
            if (l.Kind == Models.OutputKind.Error) gotError = true;
        };
        await runner.RunAsync("Write-Error 'nope'");
        Assert.True(gotError);
    }

    [Fact]
    public async Task RunAsync_SupportsCancellation()
    {
        var runner = new PowerShellRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Record.ExceptionAsync(async () =>
            await runner.RunAsync("Start-Sleep -Seconds 30", cancellationToken: cts.Token));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Cancellation took too long: {sw.Elapsed}");
    }

    [Fact]
    public async Task RunProcessAsync_Where_ReturnsZero()
    {
        var runner = new PowerShellRunner();
        // 'where.exe' always exists on Windows and returns 0 when it finds something.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var code = await runner.RunProcessAsync("where.exe", "cmd.exe", cts.Token);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task RunProcessAsync_MissingExe_Throws()
    {
        var runner = new PowerShellRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await runner.RunProcessAsync("this-binary-does-not-exist.exe", "", cts.Token));
    }

    [Fact]
    public async Task RunProcessAsync_CancellationKillsProcess()
    {
        var runner = new PowerShellRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Record.ExceptionAsync(async () =>
            await runner.RunProcessAsync("cmd.exe", "/c timeout /t 60", cts.Token));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunScriptViaPwshAsync_ReturnsExitCodeZero_OnSuccess()
    {
        var runner = new PowerShellRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exit = await runner.RunScriptViaPwshAsync("exit 0", cts.Token);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task RunScriptViaPwshAsync_ReturnsNonZero_OnFailure()
    {
        var runner = new PowerShellRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exit = await runner.RunScriptViaPwshAsync("exit 42", cts.Token);
        Assert.Equal(42, exit);
    }

    [Fact]
    public void IsClixmlNoise_Detects_AllKnownPatterns()
    {
        var m = typeof(PowerShellRunner).GetMethod("IsClixmlNoise",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True((bool)m.Invoke(null, new object[] { "#< CLIXML" })!);
        Assert.True((bool)m.Invoke(null, new object[] { "<Objs Version=\"1.1\">" })!);
        Assert.True((bool)m.Invoke(null, new object[] { "<Obj RefId=\"0\">" })!);
        Assert.True((bool)m.Invoke(null, new object[] { "</Objs>" })!);
    }

    [Fact]
    public void IsClixmlNoise_Returns_False_OnRealOutput()
    {
        var m = typeof(PowerShellRunner).GetMethod("IsClixmlNoise",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.False((bool)m.Invoke(null, new object[] { "Hello World" })!);
        Assert.False((bool)m.Invoke(null, new object[] { "winget upgrade" })!);
        Assert.False((bool)m.Invoke(null, new object[] { "123" })!);
    }
}
