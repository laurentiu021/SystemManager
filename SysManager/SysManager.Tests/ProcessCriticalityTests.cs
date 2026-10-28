// SysManager · ProcessCriticalityTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ProcessCriticality"/>: the name-plus-path checks Process Manager's kill
/// rules and the chip tooltip both key on (#2792).
/// </summary>
public class ProcessCriticalityTests
{
    private static string SystemRootExe(string bareName)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", bareName + ".exe");

    private static string ProfileFolderExe(string bareName)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), bareName + ".exe");

    [Theory]
    [InlineData("winlogon")]
    [InlineData("csrss")]
    [InlineData("lsass")]
    [InlineData("svchost")]
    public void IsBootCritical_GenuineImage_IsTrue(string bareName)
        => Assert.True(ProcessCriticality.IsBootCritical(bareName, SystemRootExe(bareName)));

    [Theory]
    [InlineData("winlogon")]
    [InlineData("csrss")]
    [InlineData("svchost")]
    public void IsBootCritical_ProfileFolderImpersonator_IsFalse(string bareName)
        // A program merely NAMED svchost.exe running from a user's profile is not the real service
        // host — the name match alone used to let it inherit both the refusal and the tooltip.
        => Assert.False(ProcessCriticality.IsBootCritical(bareName, ProfileFolderExe(bareName)));

    [Fact]
    public void IsBootCritical_UnreadablePath_StillTrustsTheName()
        // Most real system processes cannot have their path read without elevation — an empty path is
        // what the overwhelming majority of genuine boot-critical processes look like from here.
        => Assert.True(ProcessCriticality.IsBootCritical("lsass", null));

    [Fact]
    public void IsBootCritical_UnlistedName_IsFalseRegardlessOfPath()
        => Assert.False(ProcessCriticality.IsBootCritical("notepad", SystemRootExe("notepad")));

    [Theory]
    [InlineData("MsMpEng")]
    [InlineData("TrustedInstaller")]
    public void IsHighConsequence_GenuineImage_IsTrue(string bareName)
        => Assert.True(ProcessCriticality.IsHighConsequence(bareName, SystemRootExe(bareName)));

    [Theory]
    [InlineData("MsMpEng")]
    [InlineData("TrustedInstaller")]
    public void IsHighConsequence_ProfileFolderImpersonator_IsFalse(string bareName)
        => Assert.False(ProcessCriticality.IsHighConsequence(bareName, ProfileFolderExe(bareName)));

    [Fact]
    public void IsHighConsequence_UnreadablePath_StillTrustsTheName()
        => Assert.True(ProcessCriticality.IsHighConsequence("MsMpEng", null));

    [Fact]
    public void IsUnderSystemRoot_BlankPath_IsTrue()
    {
        Assert.True(ProcessCriticality.IsUnderSystemRoot(null));
        Assert.True(ProcessCriticality.IsUnderSystemRoot(""));
        Assert.True(ProcessCriticality.IsUnderSystemRoot("   "));
    }

    [Fact]
    public void IsUnderSystemRoot_DefinitePathOutsideWindows_IsFalse()
        => Assert.False(ProcessCriticality.IsUnderSystemRoot(@"C:\Users\someone\AppData\Local\thing.exe"));

    [Fact]
    public void BootCriticalAndHighConsequence_DoNotOverlap()
    {
        // Checked in order by ProcessManagerViewModel, so a name in both sets would be refused and its
        // high-consequence warning never seen — the entry would look present while being dead.
        Assert.Empty(ProcessCriticality.BootCriticalProcesses.Intersect(
            ProcessCriticality.HighConsequenceProcesses, StringComparer.OrdinalIgnoreCase));
    }
}
