// SysManager · UndoChangeRow
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.ViewModels;

/// <summary>One row of Undo Changes: a change SysManager can put back, and whether it can be put back now (#1525).</summary>
public sealed class UndoChangeRow
{
    /// <summary>Creates the row.</summary>
    /// <param name="change">The change it shows.</param>
    /// <param name="canAct">False when putting it back needs administrator rights SysManager does not have.</param>
    public UndoChangeRow(UndoChange change, bool canAct)
    {
        Change = change;
        CanAct = canAct;
    }

    /// <summary>The change the row shows.</summary>
    public UndoChange Change { get; }

    /// <summary>True when the button can be pressed: no administrator rights are needed, or SysManager has them.</summary>
    public bool CanAct { get; }

    /// <summary>The tab that made the change.</summary>
    public string Title => Change.Title;

    /// <summary>What the change was.</summary>
    public string Detail => Change.Detail;

    /// <summary>When it happened, or how it goes back.</summary>
    public string Caption => Change.Caption;

    /// <summary>What the button says.</summary>
    public string ActionText => Change.ActionText;

    /// <summary>True when putting it back needs administrator rights, which the row's pill says.</summary>
    public bool NeedsAdmin => Change.NeedsAdmin;

    /// <summary>
    /// The button's name for a screen reader. Every row's button says "Put back…" or the like, so the name carries the
    /// row's title too, the way the Dashboard's buttons name the tab they open.
    /// </summary>
    public string ButtonName => $"{Change.ActionText.TrimEnd('…')} — {Change.Title}";

    /// <summary>What the button's tooltip says, which for a button that cannot be pressed is why.</summary>
    public string ButtonHint => CanAct
        ? Change.OpensTab is null
            ? "Asks first, and says what it will change."
            : "Opens Settings Watchdog, where each setting is put back on its own."
        : "Needs administrator rights. Use Run as administrator at the top of the page.";

    /// <summary>The row's icon: the icon of the sidebar group its tab is in.</summary>
    /// <remarks>
    /// The group's, rather than one picked per row, because the window already gives these symbols a meaning, and one
    /// symbol must not mean two things in it: the bolt is the Dashboard's Quick Tune-Up, the gear the Advanced group.
    /// The sidebar's codepoints are present in both Segoe Fluent Icons and Segoe MDL2 Assets.
    /// </remarks>
    public string Glyph => Change.Kind switch
    {
        UndoChangeKind.PerformanceMode or UndoChangeKind.Services => "\uE770",  // System
        UndoChangeKind.HostsFile => "\uE968",                                   // Network, where DNS & Hosts is
        UndoChangeKind.EnvironmentVariables => "\uE713",                        // Advanced
        UndoChangeKind.GamingProfile => "\uE7FC",                               // Gaming & Profiles
        _ => "\uE9D9",                                                          // Monitor, where Settings Watchdog is
    };

    /// <summary>The row in words, which is what a screen reader announces for it.</summary>
    public override string ToString() => NeedsAdmin ? $"{Change} Needs administrator rights." : Change.ToString();
}

/// <summary>A tab whose switches are their own undo, with what they cover (#1525).</summary>
/// <param name="Title">The tab, as the sidebar names it.</param>
/// <param name="What">What its switches cover.</param>
/// <param name="NavId">The tab to open.</param>
public sealed record UndoSwitch(string Title, string What, string NavId)
{
    /// <summary>The button's name for a screen reader.</summary>
    public string ButtonName => $"Open {Title}";

    /// <summary>The line in words, which is what a screen reader announces for it.</summary>
    public override string ToString() => $"{Title}: {What}";
}
