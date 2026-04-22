// SysManager · ConsoleViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class ConsoleViewModelTests
{
    private static PowerShellLine Line(OutputKind kind, string text) => new(kind, text, DateTime.Now);

    [Fact]
    public void NewInstance_HasEmptyLines()
    {
        var vm = new ConsoleViewModel();
        Assert.Empty(vm.Lines);
    }

    [Fact]
    public void AutoScroll_DefaultsToTrue()
    {
        var vm = new ConsoleViewModel();
        Assert.True(vm.AutoScroll);
    }

    [Fact]
    public void AutoScroll_CanToggle()
    {
        var vm = new ConsoleViewModel();
        vm.AutoScroll = false;
        Assert.False(vm.AutoScroll);
        vm.AutoScroll = true;
        Assert.True(vm.AutoScroll);
    }

    [Fact]
    public void Append_AddsSingleLine()
    {
        var vm = new ConsoleViewModel();
        vm.Append(Line(OutputKind.Output, "hello"));
        Assert.Single(vm.Lines);
        Assert.Equal("hello", vm.Lines[0].Text);
    }

    [Fact]
    public void Append_PreservesOrder()
    {
        var vm = new ConsoleViewModel();
        for (int i = 0; i < 10; i++)
            vm.Append(Line(OutputKind.Output, $"line {i}"));
        for (int i = 0; i < 10; i++)
            Assert.Equal($"line {i}", vm.Lines[i].Text);
    }

    [Fact]
    public void Append_PreservesKind()
    {
        var vm = new ConsoleViewModel();
        vm.Append(Line(OutputKind.Error, "bad"));
        vm.Append(Line(OutputKind.Warning, "meh"));
        vm.Append(Line(OutputKind.Info, "fyi"));
        Assert.Equal(OutputKind.Error, vm.Lines[0].Kind);
        Assert.Equal(OutputKind.Warning, vm.Lines[1].Kind);
        Assert.Equal(OutputKind.Info, vm.Lines[2].Kind);
    }

    [Fact]
    public void Append_CapsAtMaxLines()
    {
        var vm = new ConsoleViewModel();
        // MaxLines is 5000, push 5500
        for (int i = 0; i < 5500; i++)
            vm.Append(Line(OutputKind.Output, $"{i}"));
        Assert.Equal(5000, vm.Lines.Count);
        // Oldest dropped first
        Assert.Equal("500", vm.Lines[0].Text);
        Assert.Equal("5499", vm.Lines[^1].Text);
    }

    [Fact]
    public void ClearCommand_EmptiesLines()
    {
        var vm = new ConsoleViewModel();
        for (int i = 0; i < 50; i++)
            vm.Append(Line(OutputKind.Output, $"{i}"));
        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Lines);
    }

    [Fact]
    public void ClearCommand_WithEmptyCollection_IsSafe()
    {
        var vm = new ConsoleViewModel();
        var ex = Record.Exception(() => vm.ClearCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void CopyAllCommand_DoesNotThrow_WithEmpty()
    {
        var vm = new ConsoleViewModel();
        var ex = Record.Exception(() => vm.CopyAllCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void Append_WithLongText_IsStored()
    {
        var vm = new ConsoleViewModel();
        var text = new string('x', 10_000);
        vm.Append(Line(OutputKind.Output, text));
        Assert.Equal(text, vm.Lines[0].Text);
    }

    [Fact]
    public void Append_WithEmptyText_IsStored()
    {
        var vm = new ConsoleViewModel();
        vm.Append(Line(OutputKind.Output, ""));
        Assert.Single(vm.Lines);
    }

    [Fact]
    public void Append_WithNewlinesInText_KeepsThem()
    {
        var vm = new ConsoleViewModel();
        vm.Append(Line(OutputKind.Output, "a\nb\r\nc"));
        Assert.Contains("\n", vm.Lines[0].Text);
    }

    [Fact]
    public void AllOutputKinds_AreAccepted()
    {
        var vm = new ConsoleViewModel();
        foreach (OutputKind k in Enum.GetValues(typeof(OutputKind)))
            vm.Append(Line(k, k.ToString()));
        Assert.Equal(Enum.GetValues(typeof(OutputKind)).Length, vm.Lines.Count);
    }
}
