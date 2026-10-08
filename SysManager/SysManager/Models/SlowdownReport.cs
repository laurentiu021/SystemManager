// SysManager · SlowdownReport — what the Dashboard's "Why is it slow?" check found
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>One of the five things the "Why is it slow?" check looks at (#1529), in the order its list shows them.</summary>
public enum SlowdownProbe
{
    /// <summary>Free space on the drive Windows is installed on.</summary>
    DiskSpace,

    /// <summary>The program using the most of the processor, measured twice, a second apart.</summary>
    Processor,

    /// <summary>What starts with Windows, and what Windows blamed for a slow start.</summary>
    Startup,

    /// <summary>How much of the memory is in use.</summary>
    Memory,

    /// <summary>How long Windows has run since it last restarted.</summary>
    Uptime,
}

/// <summary>Where one of the five stands while the check runs: still being looked at, read, or not readable.</summary>
/// <param name="Probe">Which of the five.</param>
/// <param name="Done">False while it is still being looked at.</param>
/// <param name="Read">True when it could be read. Says nothing until <paramref name="Done"/>.</param>
/// <param name="Summary">What was found, in a few words ("C: 96% full"). Empty unless it was read.</param>
public sealed record SlowdownProbeStatus(SlowdownProbe Probe, bool Done, bool Read, string Summary)
{
    /// <summary>The five, each still being looked at, in the order the list shows them.</summary>
    public static IReadOnlyList<SlowdownProbeStatus> Looking { get; } =
        [.. Enum.GetValues<SlowdownProbe>().Select(p => new SlowdownProbeStatus(p, Done: false, Read: false, Summary: ""))];

    /// <summary>What the list calls it.</summary>
    public string Label => Probe switch
    {
        SlowdownProbe.DiskSpace => "Disk space",
        SlowdownProbe.Processor => "What is running",
        SlowdownProbe.Startup => "Startup programs",
        SlowdownProbe.Memory => "Memory",
        _ => "Time since the last restart",
    };

    /// <summary>The line the list shows: the label while it is looked at, then what was found or that it could not be read.</summary>
    public string Line => !Done ? $"{Label}…" : Read ? $"{Label} — {Summary}" : $"{Label} — could not be read";

    /// <summary>True while it is still being looked at. Shows the spinner.</summary>
    public bool IsLooking => !Done;

    /// <summary>True once it was read. Shows the tick.</summary>
    public bool WasRead => Done && Read;

    /// <summary>True once it turned out it could not be read. Shows the warning sign.</summary>
    public bool CouldNotRead => Done && !Read;
}

/// <summary>What a finding is about, declared in the order findings are ranked, worst first.</summary>
/// <remarks>
/// The order IS the ranking: <c>SlowdownService.Rank</c> sorts on it. A nearly full system drive leads, because it
/// slows everything down and is the one cause the user can fix today. A drive that is only getting full ranks after a
/// program using the processor. The time since the last restart is always last, because it is not a problem by itself.
/// </remarks>
public enum SlowdownKind
{
    /// <summary>The system drive has so little room left that everything is slower for it.</summary>
    DriveNearlyFull,

    /// <summary>One program has been using a large share of the processor, in both samples.</summary>
    ProcessorBusy,

    /// <summary>The system drive is getting full: not yet slowing everything down, and getting there.</summary>
    DriveFilling,

    /// <summary>Many programs start with Windows, or Windows blamed some for a slow start.</summary>
    StartupHeavy,

    /// <summary>Nearly all of the memory is in use.</summary>
    MemoryFull,

    /// <summary>Windows has not restarted in more than a week.</summary>
    LongUptime,
}

/// <summary>A button on a finding. It opens the tab that can act on the finding, and does nothing else.</summary>
/// <param name="Label">What the button says.</param>
/// <param name="NavTargetId">The tab it opens.</param>
/// <param name="TabName">That tab's name, which the accessible name ends with.</param>
/// <param name="IsPrimary">True for the one action the card leads with, drawn as the primary button.</param>
public sealed record SlowdownAction(string Label, string NavTargetId, string TabName, bool IsPrimary = false)
{
    /// <summary>What a screen reader announces: the words on the button, then where it goes.</summary>
    public string AccessibleName => $"{Label} — open {TabName}";
}

/// <summary>One thing that is likely slowing the PC down: what was seen, what it means, and where to deal with it.</summary>
/// <param name="Kind">What it is about. Decides where it ranks and the colour of its stripe.</param>
/// <param name="Title">The observation, in one line: "Only 4 GB is left on the C: drive".</param>
/// <param name="Detail">What it means, in plain words, and when it is nothing to worry about.</param>
/// <param name="Actions">The tabs that can act on it. Empty when no tab here can: nothing in SysManager restarts the PC.</param>
public sealed record SlowdownFinding(SlowdownKind Kind, string Title, string Detail, IReadOnlyList<SlowdownAction> Actions)
{
    /// <summary>Where it ranks, from 1. Set when the findings are put in order.</summary>
    public int Rank { get; init; }

    /// <summary>The colour of the finding's stripe: red for a drive nearly full, grey for the restart, amber for the rest.</summary>
    public string ColorHex => Kind switch
    {
        SlowdownKind.DriveNearlyFull => StatusColors.Bad,
        SlowdownKind.LongUptime => StatusColors.Neutral,
        _ => StatusColors.Warning,
    };

    /// <summary>True when there is a tab to send the user to. Hides the row of buttons otherwise.</summary>
    public bool HasActions => Actions.Count > 0;
}

/// <summary>What one "Why is it slow?" check found.</summary>
/// <param name="Findings">What is likely slowing the PC down, worst first, ranked from 1.</param>
/// <param name="NotRead">What could not be looked at, in the order the list shows the five.</param>
/// <param name="CheckedAt">When the check was made.</param>
public sealed record SlowdownReport(IReadOnlyList<SlowdownFinding> Findings, IReadOnlyList<SlowdownProbe> NotRead,
                                    DateTime CheckedAt)
{
    /// <summary>How many things one check looks at.</summary>
    public static int ProbeCount { get; } = Enum.GetValues<SlowdownProbe>().Length;

    /// <summary>The line under the card's title: when it was checked, how much of it could be looked at, and the order.</summary>
    /// <remarks>
    /// The time rather than "just now", which would still say so an hour later. "Worst first" only when there is an order
    /// to speak of.
    /// </remarks>
    public string Summary
    {
        get
        {
            var looked = NotRead.Count == 0
                ? $"{ProbeCount} things looked at"
                : $"{ProbeCount - NotRead.Count} of {ProbeCount} things looked at";
            var order = Findings.Count > 1 ? " · worst first" : "";
            return $"Checked at {CheckedAt.ToString("HH:mm", CultureInfo.InvariantCulture)} · {looked}{order}";
        }
    }

    /// <summary>The sentence the Dashboard's status line says when the check ends, which a screen reader announces.</summary>
    public string Headline => (Findings.Count, NotRead.Count) switch
    {
        (0, 0) => "Nothing obvious is slowing this PC down.",
        (0, _) => "Nothing stood out in what could be checked.",
        (1, _) => "One thing is worth a look.",
        _ => $"{Findings.Count} things are worth a look, worst first.",
    };

    /// <summary>
    /// True when everything was looked at and nothing was found: the one state in which the card may say that nothing
    /// obvious is wrong.
    /// </summary>
    /// <remarks>
    /// Not just "no findings". A check that could not run finds nothing, and the Tune-Up card once read "All good" over
    /// checks that had not run (#2501). A diagnosis that always finds something is one nobody believes twice, and one that
    /// says "nothing" without having looked is worse.
    /// </remarks>
    public bool NothingFound => Findings.Count == 0 && NotRead.Count == 0;

    /// <summary>True when nothing was found, but something could not be looked at.</summary>
    public bool NothingFoundInWhatRan => Findings.Count == 0 && NotRead.Count > 0;

    /// <summary>True when something could not be looked at. Shows <see cref="NotCheckedDisplay"/>.</summary>
    public bool HasUncheckedItems => NotRead.Count > 0;

    /// <summary>The line naming what could not be looked at, in the words the Tune-Up card uses for its own.</summary>
    public string NotCheckedDisplay => FormatHelper.NotChecked([.. NotRead.Select(ForSentence)]);

    private static string ForSentence(SlowdownProbe probe) => probe switch
    {
        SlowdownProbe.DiskSpace => "disk space",
        SlowdownProbe.Processor => "what is running",
        SlowdownProbe.Startup => "startup programs",
        SlowdownProbe.Memory => "memory",
        _ => "the time since the last restart",
    };
}
