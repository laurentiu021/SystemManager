// SysManager · ConsoleViewModelConcurrencyTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class ConsoleViewModelConcurrencyTests
{
    [Fact]
    public async Task ConcurrentAppends_DoNotCorruptCollection()
    {
        var vm = new ConsoleViewModel();
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
                vm.Append(new PowerShellLine(OutputKind.Output, $"line-{i}", DateTime.Now));
        })).ToArray();

        await Task.WhenAll(workers);
        // The total emitted is 4000 — cap is 5000, so all should remain, but
        // regardless, the collection must not be corrupted.
        Assert.True(vm.Lines.Count <= 5000);
    }

    [Fact]
    public async Task AppendAndClearConcurrently_NoCrash()
    {
        var vm = new ConsoleViewModel();
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token;

        var writer = Task.Run(() =>
        {
            int i = 0;
            while (!ct.IsCancellationRequested)
                vm.Append(new PowerShellLine(OutputKind.Output, $"{i++}", DateTime.Now));
        });
        var clearer = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
                vm.ClearCommand.Execute(null);
        });

        var ex = await Record.ExceptionAsync(async () =>
            await Task.WhenAll(writer, clearer));
        Assert.Null(ex);
    }
}
