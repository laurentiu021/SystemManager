// SysManager · IPrivacyChoicesHandoff — carries imported privacy choices to the Privacy & Telemetry tab
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Carries the privacy choices a profile import read from Profile Export / Import to the Privacy &amp; Telemetry
/// tab, which stages them as pending changes the next time it is shown.
/// </summary>
/// <remarks>
/// A profile never writes the registry itself (#1530). The choices travel to the tab that already reviews
/// privacy changes, so the user sees every switch the profile would move and Windows changes only on Apply.
/// <para>A slot rather than a call into the other tab: that tab's view model is built the first time it is
/// opened, which may be after the import, and a tab must not hold another tab's view model. Whichever happens
/// first — the tab being shown, or its toggles finishing a load — takes the choices.</para>
/// </remarks>
public interface IPrivacyChoicesHandoff
{
    /// <summary>Leaves choices for the Privacy &amp; Telemetry tab, replacing any it has not taken yet.</summary>
    void Offer(PrivacyChoices choices);

    /// <summary>
    /// Takes the choices left for the tab, or null when there are none. Each offer is taken once, so a later
    /// visit to the tab does not stage the same choices again over whatever the user decided.
    /// </summary>
    PrivacyChoices? Take();
}

/// <summary>The one handoff the import and the Privacy &amp; Telemetry tab share, registered as a singleton.</summary>
public sealed class PrivacyChoicesHandoff : IPrivacyChoicesHandoff
{
    private PrivacyChoices? _pending;

    /// <inheritdoc/>
    public void Offer(PrivacyChoices choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        Volatile.Write(ref _pending, choices);
    }

    /// <inheritdoc/>
    public PrivacyChoices? Take() => Interlocked.Exchange(ref _pending, null);
}
