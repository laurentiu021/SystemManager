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

/// <summary>
/// Groups tests that use the shared IconExtractorService static cache.
/// </summary>
[CollectionDefinition("IconCache", DisableParallelization = true)]
public class IconCacheCollection { }

/// <summary>
/// Groups tests that swap the global <c>DialogService.Instance</c> static so they
/// run sequentially. Without this, two collections setting the shared static in
/// parallel race each other — one test's substitute receives (or misses) another's
/// Confirm call, making the confirmation-gate tests flaky.
/// </summary>
[CollectionDefinition("DialogService", DisableParallelization = true)]
public class DialogServiceCollection { }
