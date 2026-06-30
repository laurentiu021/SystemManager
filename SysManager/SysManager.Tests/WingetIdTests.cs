// SysManager · WingetIdTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Pins the shared winget package-ID validator (<see cref="WingetId.IsValid"/>),
/// which is the single source of truth used by WingetService, UninstallerService
/// and BulkInstallerService before a package ID is interpolated into a winget
/// command line. This is a security boundary (the sole barrier against
/// command-injection through a crafted ID), so both the accept and reject paths
/// are asserted directly here rather than only through the services.
/// </summary>
public class WingetIdTests
{
    [Theory]
    [InlineData("Microsoft.VisualStudioCode")]
    [InlineData("Notepad++.Notepad++")]
    [InlineData("Microsoft.VisualStudio.2022.Community")]
    [InlineData("7zip.7zip")]
    [InlineData("Mozilla.Firefox")]
    public void IsValid_AcceptsRealWorldIds(string id) =>
        Assert.True(WingetId.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pkg; calc.exe")]
    [InlineData("pkg && calc")]
    [InlineData("pkg | more")]
    [InlineData("pkg`whoami`")]
    [InlineData("pkg$(whoami)")]
    [InlineData("pkg\"quote")]
    [InlineData("pkg\nnewline")]      // trailing/embedded newline — the \z anchor must reject
    [InlineData("pkg\ttab")]
    public void IsValid_RejectsInjectionAndBlank(string? id) =>
        Assert.False(WingetId.IsValid(id));

    [Fact]
    public void IsValid_RejectsOverLengthId() =>
        Assert.False(WingetId.IsValid(new string('a', 257)));

    [Fact]
    public void IsValid_AcceptsMaxLengthId() =>
        Assert.True(WingetId.IsValid(new string('a', 256)));
}
