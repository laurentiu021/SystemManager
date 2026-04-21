using SysManager.Services;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class PowerShellRunnerDebugTests
{
    [Fact]
    public async Task RunAsync_OutputStream_FiresAtLeastOneLine_OfAnyKind()
    {
        var runner = new PowerShellRunner();
        var linesSeen = new System.Collections.Concurrent.ConcurrentBag<(Models.OutputKind Kind, string Text)>();
        runner.LineReceived += l => linesSeen.Add((l.Kind, l.Text));
        await runner.RunAsync("'hello'; Write-Output 'second'");

        Assert.NotEmpty(linesSeen);
    }

    [Fact]
    public async Task RunAsync_OutputStream_ReceivesEventsForPipeline()
    {
        // Using the pipeline ensures the DataAdded fires on the output stream.
        var runner = new PowerShellRunner();
        var any = false;
        runner.LineReceived += _ => any = true;
        await runner.RunAsync("Get-Date");
        Assert.True(any, "Did not receive any line");
    }
}
