// SysManager · DefenderExclusionValidationTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class DefenderExclusionValidationTests
{
    [Fact]
    public void Rejects_NullOrEmpty()
    {
        Assert.False(DefenderViewModel.IsValidExclusionPath(""));
        Assert.False(DefenderViewModel.IsValidExclusionPath("   "));
    }

    [Fact]
    public void Rejects_RelativePath()
        => Assert.False(DefenderViewModel.IsValidExclusionPath(@"Games\Cache"));

    [Theory]
    [InlineData(@"C:\Games\*")]
    [InlineData(@"C:\Games\?temp")]
    public void Rejects_Wildcards(string path)
        => Assert.False(DefenderViewModel.IsValidExclusionPath(path));

    [Fact]
    public void Rejects_NonexistentFolder()
        => Assert.False(DefenderViewModel.IsValidExclusionPath(@"C:\definitely\not\a\real\sysmanager\folder\xyz123"));

    [Fact]
    public void Accepts_RealRootedExistingFolder()
    {
        string temp = Path.Combine(Path.GetTempPath(), "sm-defender-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assert.True(DefenderViewModel.IsValidExclusionPath(temp));
        }
        finally
        {
            Directory.Delete(temp);
        }
    }
}
