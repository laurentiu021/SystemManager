// SysManager · TestCollections
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.IntegrationTests;

/// <summary>
/// Groups tests that touch the network stack or the process-wide WPF <c>Application</c> so they run
/// sequentially. Prevents cross-test interference when using ICMP sockets in parallel, and gives the
/// suite's single STA thread a defined end.
/// </summary>
[CollectionDefinition("Network", DisableParallelization = true)]
public class NetworkCollection : ICollectionFixture<StaThreadFixture> { }

/// <summary>
/// Ends <see cref="StaHelper"/>'s STA thread when the last test in the collection has finished.
/// </summary>
/// <remarks>
/// The thread pumps a dispatcher, which performs real layout passes over the views the tests built. Left
/// running into process teardown it takes the host down with an access violation from inside DirectWrite,
/// after every test has already reported. A collection fixture is disposed once the collection completes
/// and while the runtime is still healthy, which is the only moment a dispatcher can be shut down
/// properly — a <c>ProcessExit</c> handler was tried first and turned the crash into a hang.
/// <para>Every class that calls <see cref="StaHelper.Run"/> is in this collection, so the fixture's
/// lifetime covers all of them. Nothing injects it: a collection fixture is created before the first test
/// and disposed after the last whether or not a test class asks for it, and there is nothing here for a
/// test to use.</para>
/// </remarks>
public sealed class StaThreadFixture : IDisposable
{
    public void Dispose()
    {
        StaHelper.Stop();
        GC.SuppressFinalize(this);
    }
}
