// SysManager · DialogAnswer
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
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
/// <para>
/// <see cref="Messages"/> exists because for some gates the WORDING is the behaviour. Context Menu
/// explains a failed toggle two different ways depending on elevation, and the elevated one is the
/// valuable half — it says the entry is owned by TrustedInstaller and elevating will not help, which is
/// what stops a user restarting as administrator for nothing (#2180). Swapped, every user would take the
/// useless path and a test that counted calls would still pass.
/// </para>
/// </summary>
public sealed class DialogAnswer : IDisposable
{
    private readonly IDialogService _previous;
    private readonly ConcurrentQueue<string> _messages = new();

    /// <summary>
    /// Swaps in a substitute dialog service that always answers <paramref name="confirm"/>, keeping the
    /// previous instance for <see cref="Dispose"/> to restore.
    /// </summary>
    /// <param name="confirm">What <see cref="IDialogService.Confirm"/> returns — the user's click.</param>
    public DialogAnswer(bool confirm)
    {
        _previous = DialogService.Instance;
        var fake = Substitute.For<IDialogService>();
        fake.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
        {
            Calls++;
            _messages.Enqueue($"{call.ArgAt<string>(1)}\n{call.ArgAt<string>(0)}");
            return confirm;
        });
        DialogService.Instance = fake;
    }

    /// <summary>How many times a confirmation was actually requested.</summary>
    public int Calls { get; private set; }

    /// <summary>
    /// Every confirmation shown, as <c>"title\nmessage"</c>, in the order they were requested.
    /// </summary>
    /// <remarks>
    /// Title and body joined rather than kept apart, because a caller asserting on wording wants to know
    /// the text reached the user and does not care which half carried it. A
    /// <see cref="ConcurrentQueue{T}"/> because a gate reached from an <c>await</c> continuation raises
    /// this on a thread-pool thread, not the test's.
    /// </remarks>
    public IReadOnlyCollection<string> Messages => _messages;

    public void Dispose() => DialogService.Instance = _previous;
}
