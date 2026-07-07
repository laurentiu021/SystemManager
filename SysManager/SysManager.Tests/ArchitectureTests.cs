// SysManager · ArchitectureTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using NetArchTest.Rules;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Architecture fitness functions (NetArchTest) that pin the MVVM dependency direction
/// — View → ViewModel → Service/Model — so a change can't silently reintroduce an upward
/// reference (the class of regression that let the Dashboard shell winget directly instead
/// of going through the injected service). They run in CI against the shipped assembly, so
/// the layering is enforced mechanically rather than by review discipline alone.
/// </summary>
public class ArchitectureTests
{
    // Any public type from the app assembly anchors NetArchTest to SysManager.dll.
    private static Assembly AppAssembly => typeof(WingetService).Assembly;

    private static void AssertNoDependency(string fromNamespace, string onNamespace)
    {
        var result = Types.InAssembly(AppAssembly)
            .That().ResideInNamespace(fromNamespace)
            .ShouldNot().HaveDependencyOn(onNamespace)
            .GetResult();

        var offenders = result.FailingTypes is null
            ? string.Empty
            : string.Join(", ", result.FailingTypes.Select(t => t.FullName));
        Assert.True(result.IsSuccessful,
            $"{fromNamespace} must not depend on {onNamespace}. Offending types: {offenders}");
    }

    [Fact]
    public void Services_DoNotDependOn_ViewModels()
        => AssertNoDependency("SysManager.Services", "SysManager.ViewModels");

    [Fact]
    public void Services_DoNotDependOn_Views()
        => AssertNoDependency("SysManager.Services", "SysManager.Views");

    [Fact]
    public void ViewModels_DoNotDependOn_Views()
        => AssertNoDependency("SysManager.ViewModels", "SysManager.Views");

    [Theory]
    [InlineData("SysManager.Services")]
    [InlineData("SysManager.ViewModels")]
    [InlineData("SysManager.Views")]
    public void Models_DoNotDependOnUpperLayers(string upperLayer)
        => AssertNoDependency("SysManager.Models", upperLayer);
}
