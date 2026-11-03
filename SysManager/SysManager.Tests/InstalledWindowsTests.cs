// SysManager · InstalledWindowsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// <see cref="InstalledWindows"/> reads the edition and build under the HKLM root it is given (#2778). These give it a
/// throwaway HKCU key as that root, so what it reads is what the test wrote there.
/// </summary>
public sealed class InstalledWindowsTests : IDisposable
{
    private readonly string _rootName = @"Software\SysManagerTests\InstalledWindows_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public InstalledWindowsTests() => _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);
    }

    private void Write(string? editionId, string? build)
    {
        using var key = _root.CreateSubKey(InstalledWindows.CurrentVersionPath, writable: true);
        if (editionId is not null) key.SetValue("EditionID", editionId);
        if (build is not null) key.SetValue("CurrentBuildNumber", build);
    }

    [Fact]
    public void ReadsTheEditionAndTheBuild()
    {
        Write("Professional", "19045");

        Assert.Equal(new InstalledWindows("Professional", 19045), InstalledWindows.Read(_root));
    }

    [Fact]
    public void WithNoKeyToReadFrom_KnowsNeither()
        => Assert.Equal(new InstalledWindows(null, null), InstalledWindows.Read(_root));

    [Theory]
    [InlineData("")]
    [InlineData("22000.1")]
    [InlineData("-19045")]
    [InlineData("build 19045")]
    public void ABuildThatIsNotANumber_IsNotRead(string build)
    {
        Write("Core", build);

        Assert.Equal(new InstalledWindows("Core", null), InstalledWindows.Read(_root));
    }

    [Theory]
    [InlineData(19045, true)]
    [InlineData(21999, true)]
    [InlineData(22000, false)]
    [InlineData(26100, false)]
    public void Windows10_IsEveryBuildBeforeWindows11s_First(int build, bool windows10)
        => Assert.Equal(windows10, new InstalledWindows(null, build).IsWindows10);

    [Fact]
    public void ABuildThatCouldNotBeRead_CountsAsWindows11_AsEverySwitchAssumedBefore()
        => Assert.False(new InstalledWindows("Core", null).IsWindows10);
}
