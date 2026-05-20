// SysManager · TestCollections
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Tests;

/// <summary>
/// Groups tests that touch the network stack so they run sequentially.
/// Prevents cross-test interference when using ICMP sockets in parallel.
/// </summary>
[CollectionDefinition("Network", DisableParallelization = true)]
public class NetworkCollection { }

/// <summary>
/// Groups tests that use the shared OperationLockService singleton so they
/// run sequentially and avoid cross-test lock contention.
/// </summary>
[CollectionDefinition("OperationLock", DisableParallelization = true)]
public class OperationLockCollection { }
