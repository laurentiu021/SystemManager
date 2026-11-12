// SysManager · ProcessCriticalityWorkingFolderTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// A path that is not a full one is not judged against the folder SysManager runs in. The working folder is set to the
/// System folder, where a relative name would otherwise resolve to a Windows program.
/// </summary>
/// <remarks>
/// In <c>ProcessEnvironment</c>, which runs alone, because the working folder belongs to the whole process.
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class ProcessCriticalityWorkingFolderTests
{
    private static void FromTheSystemFolder(Action check)
    {
        var before = Environment.CurrentDirectory;
        Environment.CurrentDirectory = Environment.SystemDirectory;
        try { check(); }
        finally { Environment.CurrentDirectory = before; }
    }

    [Theory]
    [InlineData("Widgets.exe")]
    [InlineData("svchost.exe")]
    [InlineData(@"wbem\WmiPrvSE.exe")]
    public void ARelativePath_IsNeverCalledWhereWindowsKeepsItsPrograms(string relative)
        => FromTheSystemFolder(() => Assert.False(ProcessCriticality.IsWhereWindowsKeepsItsPrograms(relative)));

    /// <summary>The refusal keeps its own safe direction: a path it cannot place keeps refusing.</summary>
    [Fact]
    public void ARelativePath_KeepsTheRefusal()
        => FromTheSystemFolder(() => Assert.True(ProcessCriticality.IsBootCritical("svchost", "svchost.exe")));
}
