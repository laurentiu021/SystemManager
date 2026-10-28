// SysManager · ProcessEntry — model for running processes
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// A running Windows process with its resource usage.
/// </summary>
public sealed partial class ProcessEntry : ObservableObject
{
    [ObservableProperty] private int _pid;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SafetyTooltip))]
    private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryDisplay))]
    private long _memoryBytes;
    [ObservableProperty] private string _status = "";
    // No UserName: one was declared here and nothing ever assigned it, so it could only ever have shown
    // an empty owner. Reading a process's owner needs OpenProcessToken per process, which Windows denies
    // for other users' and most system processes unless elevated — so the choices were to implement it
    // properly or not to imply it exists. Dropped; the Safety column already answers the question this
    // tab is for ("is this Windows, or something I installed?").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartTimeDisplay))]
    private DateTime _startTime;
    [ObservableProperty] private int _threadCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SafetyTooltip))]
    private string _filePath = "";
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _hasMainWindow;
    [ObservableProperty] private string _plainDescription = "";
    [ObservableProperty] private string _category = "Unknown";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SafetyTooltip))]
    private string _safetyLevel = "Unknown";

    /// <summary>
    /// Whether Windows can confirm who made the running image, from its certificate.
    /// </summary>
    /// <remarks>
    /// A different question from <see cref="SafetyLevel"/>, which comes from the bundled process database
    /// and answers "is this Windows, or something I installed?" by name. This one answers "is this really
    /// from who it says?" from the file itself, so a copy of <c>svchost.exe</c> sitting in a user folder
    /// cannot inherit the real one's reputation.
    /// <para>Defaults to <see cref="SignatureTrust.Unknown"/>, which renders no pill: a process whose image
    /// path could not be read — most system processes, without elevation — has not been checked, and a grey
    /// chip there would be a verdict it has not earned.</para>
    /// </remarks>
    [ObservableProperty] private SignatureTrust _signature = SignatureTrust.Unknown;

    /// <summary>The sentence behind <see cref="Signature"/>, shown on hover. Empty when nothing was checked.</summary>
    [ObservableProperty] private string _signatureDetail = "";

    /// <summary>True when the process has a valid, accessible file path (cached on creation).</summary>
    [ObservableProperty] private bool _canOpenFileLocation;

    /// <summary>Formatted memory for display.</summary>
    public string MemoryDisplay => FormatHelper.FormatSize(MemoryBytes);

    /// <summary>When the process started, or <c>—</c> when Windows would not say.</summary>
    /// <remarks>
    /// Same format and same em-dash fallback as <see cref="FileLocker.StartTimeDisplay"/>, which was the only
    /// place in the app showing a process start time before this one.
    /// <para>The fallback is not cosmetic. <c>Process.StartTime</c> throws for most system processes without
    /// elevation, and <see cref="SysManager.Services.ProcessManagerService"/> swallows that and leaves the field at
    /// <c>default</c> — so binding the raw value would print <c>0001-01-01 00:00:00</c> in a column, which
    /// reads as a bug rather than as "not available".</para>
    /// <para>Absolute rather than a relative age ("4 min ago"), deliberately. The list refreshes through
    /// <c>ProcessManagerViewModel.ReconcileInto</c>, which only writes properties whose value CHANGED — a
    /// relative string derives from the clock rather than from the model, so it would be computed once when
    /// the row appeared and then never raise a change again. It would silently freeze at the age the process
    /// had when it was first seen, which is worse than an absolute timestamp that is simply always true.</para>
    /// </remarks>
    public string StartTimeDisplay => StartTime == default
        ? "—"
        : StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// The safety chip's tooltip. Tiered above the generic wording <see cref="ProcessSafetyPalette.Tooltip"/>
    /// gives every "Windows" row.
    /// </summary>
    /// <remarks>
    /// <see cref="ProcessSafetyPalette.Tooltip"/> told every System-tier row "ending it will not crash your
    /// PC" — true for most of the database, but false for <see cref="ProcessCriticality.BootCriticalProcesses"/>
    /// (which Process Manager actually refuses to end, for exactly that reason) and understated for
    /// <see cref="ProcessCriticality.HighConsequenceProcesses"/>. This checks those two tiers by name AND path
    /// first — the same verification <c>ProcessManagerViewModel.IsKernelCritical</c>/<c>IsHighConsequence</c>
    /// apply to the refusal and the warning prompt — and falls back to the generic wording only when neither
    /// tier matches, so the tooltip never promises something the rest of the tab contradicts (#2792).
    /// </remarks>
    public string SafetyTooltip
    {
        get
        {
            var bareName = Path.GetFileNameWithoutExtension(Name);
            if (ProcessCriticality.IsBootCritical(bareName, FilePath))
                return "A core Windows process. Ending it will cause a system crash (BSOD) or an immediate " +
                       "sign-out, which is why Process Manager refuses to end it.";
            if (ProcessCriticality.IsHighConsequence(bareName, FilePath))
                return "A Windows security or servicing process. Ending it can disable protection or corrupt " +
                       "an in-progress update — confirm you mean to before continuing.";
            return ProcessSafetyPalette.Tooltip(SafetyLevel);
        }
    }
}
