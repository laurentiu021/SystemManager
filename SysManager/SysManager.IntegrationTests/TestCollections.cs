namespace SysManager.IntegrationTests;

/// <summary>
/// Groups tests that touch the network stack so they run sequentially.
/// Prevents cross-test interference when using ICMP sockets in parallel.
/// </summary>
[CollectionDefinition("Network", DisableParallelization = true)]
public class NetworkCollection { }
