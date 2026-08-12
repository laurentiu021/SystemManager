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
/// Groups tests that use the shared IconExtractorService static cache.
/// </summary>
[CollectionDefinition("IconCache", DisableParallelization = true)]
public class IconCacheCollection { }

/// <summary>
/// Groups every test class that touches a process-wide mutable singleton — <c>DialogService.Instance</c>
/// or <c>OperationLockService.Instance</c> — so they run sequentially.
/// </summary>
/// <remarks>
/// <para>
/// Without serialization, two classes setting <c>DialogService.Instance</c> in parallel race each other:
/// one test's substitute receives (or misses) another's Confirm call, so a confirmation gate answers with
/// a foreign canned answer and a destructive-op test passes for the wrong reason.
/// </para>
/// <para>
/// This replaces two separate collections, "DialogService" and "OperationLock", and the split was itself
/// the defect rather than untidiness. <c>parallelizeTestCollections</c> is true, so two DIFFERENT
/// serialized collections still run in parallel with EACH OTHER — and <c>PerformanceViewModelTests</c>
/// and <c>ShortcutCleanerViewModelTests</c> touch both singletons. xUnit allows a class only one
/// collection, so with two names those classes were serialized against the dialog group while racing the
/// lock group, whichever name they picked. No correct answer was available.
/// </para>
/// <para>
/// One collection spanning both statics is the only shape that can express "serialize against everything
/// sharing this state". The cost is roughly 500 tests running sequentially instead of 460; the benefit is
/// that a test asserting which operation holds the lock can no longer observe a foreign one.
/// </para>
/// </remarks>
[CollectionDefinition("ProcessWideStatics", DisableParallelization = true)]
public class ProcessWideStaticsCollection { }

/// <summary>
/// Groups tests that temporarily replace process environment variables.
/// </summary>
[CollectionDefinition("ProcessEnvironment", DisableParallelization = true)]
public class ProcessEnvironmentCollection { }
