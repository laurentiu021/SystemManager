// SysManager · TestCollections
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

namespace SysManager.Tests;

/// <summary>
/// Groups tests that touch the network stack so they run sequentially.
/// Prevents cross-test interference when using ICMP sockets in parallel.
/// </summary>
[CollectionDefinition("Network", DisableParallelization = true)]
public class NetworkCollection { }
