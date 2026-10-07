// SysManager · IPutBackSignal — tells the tab that made a change that Undo Changes has put it back
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Tells the tab that made a change that Undo Changes has put it back, so that tab reads its state again (#1525).
/// </summary>
/// <remarks>
/// Every tab here is built once and kept for the session, and each keeps what it last read. After Undo Changes put
/// a change back, the tab that made it still showed the change, and two of them did worse than show it. Performance
/// Mode kept its record of the original settings in memory after the file was gone, so its next change recorded no
/// original at all, and Gaming Profile kept believing game mode was on, which keeps its Start button off.
/// <para>An event rather than a call into the other tab, for the reason <see cref="IPrivacyChoicesHandoff"/> gives:
/// a tab must not hold another tab's view model. A tab that has not been opened yet hears nothing, and needs
/// nothing: it reads what is there when it is built.</para>
/// <para>Raised only by Undo Changes. A tab's own put-back already reads its state again, and hearing about it a
/// second time would only race its own status line.</para>
/// <para>Raised whether or not the put-back worked, because one that stopped part-way has still changed something. So a
/// tab that hears it says only what it found when it read its state again: whether the put-back worked is said on Undo
/// Changes, which knows. Not raised for one that was refused before anything was written: there is nothing new to read,
/// and a tab such as DNS &amp; Hosts would drop what is being edited on it to read it.</para>
/// </remarks>
public interface IPutBackSignal
{
    /// <summary>
    /// Raised after Undo Changes put a change back, or tried to and may have changed something, on the thread that tried.
    /// </summary>
    event Action<UndoChangeKind>? PutBack;

    /// <summary>Tells every tab listening that the change of <paramref name="kind"/> was put back, or tried.</summary>
    void Raise(UndoChangeKind kind);
}

/// <summary>The one signal Undo Changes and the tabs it puts changes back for share, registered as a singleton.</summary>
public sealed class PutBackSignal : IPutBackSignal
{
    /// <inheritdoc/>
    public event Action<UndoChangeKind>? PutBack;

    /// <inheritdoc/>
    public void Raise(UndoChangeKind kind) => PutBack?.Invoke(kind);
}
