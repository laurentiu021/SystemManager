// SysManager · PipelineStoppedDetectionTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management.Automation;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Which exceptions count as "the pipeline was stopped" — the detail that made #2206 invisible locally and
/// permanent on CI.
/// </summary>
/// <remarks>
/// Cancelling a PowerShell run calls <c>ps.Stop()</c>, and the exception that surfaces depends on WHERE the
/// pipeline ran. Unelevated, the runspace is in-process and <c>EndInvoke</c> throws
/// <see cref="PipelineStoppedException"/> directly. Elevated, the runspace is an out-of-process Windows
/// PowerShell 5.1 child reached over remoting, and the failure comes back as a
/// <see cref="RemoteException"/> carrying a serialized copy.
///
/// <para>The runner's translation arm named the in-process type only, so on an elevated runspace it never
/// matched and cancellation escaped as a raw PowerShell error rather than as
/// <c>OperationCanceledException</c>. A developer machine runs unelevated and cannot see that; CI runs as
/// an administrator and hit it repeatedly, reporting
/// <c>RemoteException (The pipeline has been stopped.)</c>.</para>
///
/// <para>These are unit tests over the predicate rather than integration tests over a real remote failure,
/// because producing one needs an elevated host. That is the point: the shape CI produced is asserted here
/// on any machine, at no cost, instead of being discoverable only by shipping.</para>
/// </remarks>
public class PipelineStoppedDetectionTests
{
    [Fact]
    public void TheInProcessShape_IsRecognised()
        => Assert.True(PowerShellRunner.IsPipelineStopped(new PipelineStoppedException()));

    /// <summary>
    /// A wrapped one counts too — <c>Task.Factory.FromAsync</c> and the runspace plumbing both wrap.
    /// </summary>
    [Fact]
    public void AWrappedOne_IsRecognised()
        => Assert.True(PowerShellRunner.IsPipelineStopped(
            new InvalidOperationException("outer", new PipelineStoppedException())));

    /// <summary>
    /// The remoted shape, built the way remoting actually delivers it: a <c>PSObject</c> whose
    /// <c>TypeNames</c> carry the <c>Deserialized.</c> prefix.
    /// </summary>
    /// <remarks>
    /// This is the case that would have been missed by an obvious implementation. Remoting does NOT hand
    /// back the original exception — it rehydrates a <c>PSObject</c> typed
    /// <c>Deserialized.System.Management.Automation.PipelineStoppedException</c>, so
    /// <c>SerializedRemoteException.BaseObject is PipelineStoppedException</c> is false and only the type
    /// NAME identifies it. Asserting the deserialized prefix explicitly is what pins that.
    /// </remarks>
    [Fact]
    public void TheRemotedShape_IsRecognisedByItsDeserializedTypeName()
    {
        var serialized = new PSObject(new object());
        serialized.TypeNames.Clear();
        serialized.TypeNames.Add("Deserialized.System.Management.Automation.PipelineStoppedException");
        serialized.TypeNames.Add("Deserialized.System.Management.Automation.RuntimeException");

        var remote = BuildRemoteException(serialized);
        Assert.True(PowerShellRunner.IsPipelineStopped(remote),
            "the remoted stopped-pipeline shape was not recognised. Cancelling an ELEVATED run then escapes "
            + "as a raw PowerShell error instead of OperationCanceledException, which is #2206.");
    }

    /// <summary>An unrelated failure must NOT be reported as a cancellation, even one from remoting.</summary>
    /// <remarks>
    /// Without this the predicate could be "return true" and every test above would still pass — and a real
    /// script error during a cancelled run would be silently relabelled as "cancelled", hiding it from the
    /// user and from the log.
    /// </remarks>
    [Fact]
    public void AnUnrelatedRemoteFailure_IsNotRecognised()
    {
        var serialized = new PSObject(new object());
        serialized.TypeNames.Clear();
        serialized.TypeNames.Add("Deserialized.System.UnauthorizedAccessException");

        Assert.False(PowerShellRunner.IsPipelineStopped(BuildRemoteException(serialized)));
    }

    [Fact]
    public void AnUnrelatedException_IsNotRecognised()
    {
        Assert.False(PowerShellRunner.IsPipelineStopped(new UnauthorizedAccessException("denied")));
        Assert.False(PowerShellRunner.IsPipelineStopped(
            new InvalidOperationException("outer", new TimeoutException())));
    }

    // ── The out-of-process transport's own way of saying "stopped" (#2286) ──
    //
    // Closing the lost-stop window meant the stop started landing on the elevated out-of-process transport,
    // where EndInvoke throws PSRemotingDataStructureException("The remote pipeline has been stopped.")
    // rather than the PipelineStoppedException an in-process runspace raises. So a cancellation that now
    // WORKED surfaced as a raw remoting error. CI found it; this workstation's in-process runspace cannot.

    [Fact]
    public void ARemotingDataStructureFault_IsRecognisedAsOurStop()
    {
        Assert.True(PowerShellRunner.IsRemotingTornDownByOurStop(
            new System.Management.Automation.Remoting.PSRemotingDataStructureException(
                "The remote pipeline has been stopped.")));
    }

    [Fact]
    public void ARemotingDataStructureFault_IsRecognisedWhenWrapped()
    {
        // FromAsync surfaces EndInvoke's exception directly, but a transport can nest it — the sibling
        // predicate unwraps one level for the same reason and this must not be the weaker of the two.
        Assert.True(PowerShellRunner.IsRemotingTornDownByOurStop(
            new InvalidOperationException("outer",
                new System.Management.Automation.Remoting.PSRemotingDataStructureException("stopped"))));
    }

    [Fact]
    public void ARemotingFault_IsNotTreatedAsAStopByTheStrictPredicate()
    {
        // The two predicates stay separate on purpose. IsPipelineStopped answers "does this exception mean
        // the pipeline was stopped", and a general remoting fault does not — folding it in would make a real
        // transport breakdown read as a user cancellation. What makes the weaker inference safe is the call
        // site's `cancellationToken.IsCancellationRequested`, not the type.
        Assert.False(PowerShellRunner.IsPipelineStopped(
            new System.Management.Automation.Remoting.PSRemotingDataStructureException(
                "The remote pipeline has been stopped.")));
    }

    [Fact]
    public void AnUnrelatedException_IsNotTakenForOurStop()
    {
        Assert.False(PowerShellRunner.IsRemotingTornDownByOurStop(new TimeoutException("timed out")));
        Assert.False(PowerShellRunner.IsRemotingTornDownByOurStop(new PipelineStoppedException()));
    }

    /// <summary>
    /// Builds a <see cref="RemoteException"/> carrying <paramref name="serialized"/>.
    /// </summary>
    /// <remarks>
    /// Its constructors that take a serialized payload are not public, so this goes through reflection.
    /// Deliberately fails loudly rather than returning something approximate: if the shape ever changes,
    /// these tests must stop and say so instead of quietly asserting over a plain exception and passing for
    /// the wrong reason.
    /// </remarks>
    private static RemoteException BuildRemoteException(PSObject serialized)
    {
        var ctor = typeof(RemoteException).GetConstructors(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public)
            .FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length >= 2
                       && p[0].ParameterType == typeof(string)
                       && p.Any(x => x.ParameterType == typeof(PSObject));
            });

        Assert.NotNull(ctor);   // the shape changed — fix this helper rather than skipping the case

        var args = ctor!.GetParameters().Select(p =>
            p.ParameterType == typeof(string) ? (object?)"The pipeline has been stopped."
            : p.ParameterType == typeof(PSObject) ? serialized
            : null).ToArray();

        var built = (RemoteException)ctor.Invoke(args);
        Assert.NotNull(built.SerializedRemoteException);   // the payload really is attached
        return built;
    }
}
