// SysManager · ProcessEntryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ProcessEntry.SafetyTooltip"/>: the chip tooltip must match the refusal and
/// warning tiers <c>ProcessManagerViewModel</c> actually applies, not the generic "Windows" wording
/// every System-tier row used to get regardless of consequence (#2792).
/// </summary>
public class ProcessEntryTests
{
    private static string SystemRootExe(string bareName)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", bareName + ".exe");

    [Fact]
    public void SafetyTooltip_BootCritical_WarnsOfTheRefusal_NotTheGenericReassurance()
    {
        var entry = new ProcessEntry
        {
            Name = "csrss.exe",
            FilePath = SystemRootExe("csrss"),
            SafetyLevel = nameof(ProcessSafety.System),
        };

        Assert.Contains("system crash", entry.SafetyTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("will not crash", entry.SafetyTooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafetyTooltip_HighConsequence_NamesTheRealCost_NotTheGenericReassurance()
    {
        var entry = new ProcessEntry
        {
            Name = "MsMpEng.exe",
            FilePath = SystemRootExe("MsMpEng"),
            SafetyLevel = nameof(ProcessSafety.System),
        };

        Assert.Contains("security or servicing", entry.SafetyTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("will not crash", entry.SafetyTooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafetyTooltip_WindowsComponentButNeitherTier_FallsBackToTheGenericWording()
    {
        // notepad.exe is tagged "System" in the database but is in neither criticality tier — this is
        // the ordinary case the generic wording is actually true for.
        var entry = new ProcessEntry
        {
            Name = "notepad.exe",
            FilePath = SystemRootExe("notepad"),
            SafetyLevel = nameof(ProcessSafety.System),
        };

        Assert.Contains("will not crash", entry.SafetyTooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafetyTooltip_BootCriticalNameFromAProfileFolder_DoesNotGetTheRefusalWarning()
    {
        // A program merely named csrss.exe running from a profile folder is not the real one — it must
        // not borrow the boot-critical tooltip either.
        var entry = new ProcessEntry
        {
            Name = "csrss.exe",
            FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "csrss.exe"),
            SafetyLevel = nameof(ProcessSafety.Unknown),
        };

        Assert.DoesNotContain("system crash", entry.SafetyTooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafetyTooltip_TrustedApp_UsesTheTrustedWording()
    {
        var entry = new ProcessEntry
        {
            Name = "chrome.exe",
            SafetyLevel = nameof(ProcessSafety.Trusted),
        };

        Assert.Contains("Safe to end", entry.SafetyTooltip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(ProcessEntry.Name))]
    [InlineData(nameof(ProcessEntry.FilePath))]
    [InlineData(nameof(ProcessEntry.SafetyLevel))]
    public void SafetyTooltip_RaisesPropertyChanged_WhenAnyOfItsInputsChange(string changedProperty)
    {
        // ReconcileInto only writes properties whose value changed, same as every other row property —
        // a bound tooltip that never raised its own PropertyChanged would freeze at whatever it first
        // evaluated to, exactly the StartTimeDisplay class of bug this model already guards elsewhere.
        var entry = new ProcessEntry { Name = "csrss.exe", SafetyLevel = nameof(ProcessSafety.System) };
        var raised = entry.RecordPropertyChanges();

        switch (changedProperty)
        {
            case nameof(ProcessEntry.Name): entry.Name = "other.exe"; break;
            case nameof(ProcessEntry.FilePath): entry.FilePath = SystemRootExe("csrss"); break;
            case nameof(ProcessEntry.SafetyLevel): entry.SafetyLevel = nameof(ProcessSafety.Unknown); break;
        }

        Assert.Contains(nameof(ProcessEntry.SafetyTooltip), raised);
    }
}
