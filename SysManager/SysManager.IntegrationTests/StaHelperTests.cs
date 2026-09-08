// SysManager · StaHelperTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;

namespace SysManager.IntegrationTests;

/// <summary>
/// Pins what <see cref="StaHelper"/> guarantees: one STA thread for the whole suite, and the action's
/// exception reaching the caller.
/// </summary>
/// <remarks>
/// Each of these was a property the previous thread-per-call version either lacked or provided by
/// accident, and the first is the one that produced five failures the moment the suite ran to completion
/// (#2156).
/// <para>Nothing here asserts that a dispatcher runs, on purpose. The thread deliberately does not pump —
/// see <see cref="StaHelper"/> for the three attempts that did and what each of them broke.</para>
/// </remarks>
[Collection("Network")] // shares the process-wide Application with the other Windows-level tests
public class StaHelperTests
{
    /// <summary>
    /// A styled WPF element can be built in one <c>Run</c> and again in the next.
    /// </summary>
    /// <remarks>
    /// THE regression test. App-scope resources are <c>DispatcherObject</c>s owned by the thread that
    /// created the <c>Application</c>; with a thread per call, the second build resolved a Style belonging
    /// to a thread that had already exited and threw <c>InvalidOperationException: The calling thread
    /// cannot access this object because a different thread owns it</c>, wrapped in a
    /// <c>XamlParseException</c>.
    /// <para>Applying the style is what matters, not finding it. <c>TryFindResource</c> returns the object
    /// without touching a thread-affine member, which is why
    /// <c>AppResourcesTests.Ensure_StillResolvesStylesOnASecondCall</c> passed throughout while four
    /// <c>AboutViewUiTests</c> failed — so this assigns it to a real element and lets WPF's own thread
    /// check run.</para>
    /// </remarks>
    [Fact]
    public void Run_AStyledElementBuildsInTwoSeparateCalls()
    {
        static void BuildOne()
        {
            AppResources.Ensure();
            var border = new Border { Style = (Style)Application.Current!.FindResource("Card") };
            Assert.NotNull(border.Style);
        }

        StaHelper.Run(BuildOne);
        StaHelper.Run(BuildOne);   // threw here, every time, before one thread was shared
    }

    [Fact]
    public void Run_UsesTheSameThreadEveryTime_AndNotTheCallers()
    {
        var first = 0;
        var second = 0;

        StaHelper.Run(() => first = Environment.CurrentManagedThreadId);
        StaHelper.Run(() => second = Environment.CurrentManagedThreadId);

        Assert.Equal(first, second);
        Assert.NotEqual(Environment.CurrentManagedThreadId, first);
    }

    /// <summary>
    /// The shared thread is an STA thread, which is the reason it exists.
    /// </summary>
    /// <remarks>
    /// WPF refuses to construct a visual on an MTA thread, so a shared thread that lost its apartment
    /// state would fail every view test with a different and far less obvious error than the one this
    /// change is about. One line to assert, and it pins the one property of the thread that no other test
    /// here would notice going missing.
    /// </remarks>
    [Fact]
    public void Run_RunsOnAnStaThread()
    {
        var state = ApartmentState.Unknown;

        StaHelper.Run(() => state = Thread.CurrentThread.GetApartmentState());

        Assert.Equal(ApartmentState.STA, state);
    }

    /// <summary>
    /// The action's exception reaches the caller, with its type and message.
    /// </summary>
    /// <remarks>
    /// The old version captured it into a local and rethrew after <c>Join</c>. <c>Dispatcher.Invoke</c>
    /// does the same thing, and this asserts it rather than assuming the framework's behaviour matches the
    /// hand-rolled one it replaced — a swallowed test failure is the worst outcome available here.
    /// </remarks>
    [Fact]
    public void Run_PropagatesTheActionsExceptionToTheCaller()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => StaHelper.Run(() => throw new InvalidOperationException("raised on the STA thread")));

        Assert.Equal("raised on the STA thread", thrown.Message);
    }

    [Fact]
    public void Run_RejectsANullAction()
        => Assert.Throws<ArgumentNullException>(() => StaHelper.Run(null!));
}
