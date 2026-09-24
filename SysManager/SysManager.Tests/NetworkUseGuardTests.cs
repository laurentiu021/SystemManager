// SysManager · NetworkUseGuardTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Net;
using System.Net.Sockets;

namespace SysManager.Tests;

/// <summary>
/// Proves on every run that <see cref="NetworkUseGuard"/> is actually listening.
/// </summary>
/// <remarks>
/// A guard that finds nothing reads exactly like a guard that is not running. It was proven red locally by
/// putting a real GitHub call back, but CI runs the suite under <c>dotnet test</c> with coverage, and a silent
/// guard there would gate nothing while looking green. This class makes the difference visible in every
/// environment: constructing it needs the assembly fixture, and the test needs the fixture to hear a lookup.
/// </remarks>
public sealed class NetworkUseGuardTests(NetworkUseGuard guard)
{
    [Fact]
    public async Task TheGuardHearsALookupAndChargesItToTheTestThatMadeIt()
    {
        // The verdict exempts this test by name. If the name and the exemption ever drift apart, this lookup
        // would fail the whole run — so the mismatch is caught here first, with a message that says why.
        Assert.Equal(NetworkUseGuard.LivenessProbe,
            $"{typeof(NetworkUseGuardTests).FullName}.{nameof(TheGuardHearsALookupAndChargesItToTheTestThatMadeIt)}");

        // localhost goes through the OS resolver, which raises the same event a real lookup does, without
        // leaving the machine. Whether it resolves is irrelevant: the event is raised before the answer.
        try { await Dns.GetHostAddressesAsync("localhost", TestContext.Current.CancellationToken); }
        catch (SocketException) { /* the outcome of the lookup is not what this asserts */ }

        Assert.Contains(guard.ReachedBy(NetworkUseGuard.LivenessProbe),
            entry => entry.EndsWith(": resolved localhost", StringComparison.Ordinal));
    }
}
