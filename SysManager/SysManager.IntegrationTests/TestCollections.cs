// SysManager · TestCollections
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.IntegrationTests;

/// <summary>
/// Groups tests that touch the network stack or the process-wide WPF <c>Application</c> so they run
/// sequentially. Prevents cross-test interference when using ICMP sockets in parallel, and keeps the
/// tests that share <see cref="StaHelper"/>'s single STA thread from overlapping.
/// </summary>
[CollectionDefinition("Network", DisableParallelization = true)]
public class NetworkCollection { }
