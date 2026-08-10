// SysManager · DialogAnswer
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Scopes a canned answer over the <see cref="DialogService.Instance"/> singleton and restores
/// the previous instance on dispose, so a test can drive a confirmation gate without a UI.
/// <para>
/// The restore matters: <see cref="DialogService.Instance"/> is process-wide static state, so a
/// test that swapped it and threw would leak the substitute into every later test in the same
/// collection. Wrapping it in a <c>using</c> makes the restore exception-safe.
/// </para>
/// <para>
/// <see cref="Calls"/> exists so a test can prove a dialog was NOT shown — asserting on the
/// side effect alone cannot tell "the user said yes" apart from "no gate ran at all".
/// </para>
/// </summary>
public sealed class DialogAnswer : IDisposable
{
    private readonly IDialogService _previous;

    /// <param name="confirm">What <see cref="IDialogService.Confirm"/> returns — the user's click.</param>
    public DialogAnswer(bool confirm)
    {
        _previous = DialogService.Instance;
        var fake = Substitute.For<IDialogService>();
        fake.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(_ => { Calls++; return confirm; });
        DialogService.Instance = fake;
    }

    /// <summary>How many times a confirmation was actually requested.</summary>
    public int Calls { get; private set; }

    public void Dispose() => DialogService.Instance = _previous;
}
