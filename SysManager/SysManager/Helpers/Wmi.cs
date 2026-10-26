// SysManager · Wmi — the one place WMI reads are given a time limit
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;

namespace SysManager.Helpers;

/// <summary>
/// Builds every <see cref="ManagementObjectSearcher"/> in the app, with a bounded read.
/// </summary>
/// <remarks>
/// <para>WMI's default <see cref="EnumerationOptions"/> has an INFINITE timeout, and a provider that stops
/// answering is an ordinary Windows fault, not an exotic one: a failing disk, a USB bridge that dropped off, a
/// degraded Storage Spaces pool. Every search in the app used that default except the reliability history and the
/// adapter-key read, so one stalled provider could hold a reader for as long as SysManager stayed open. On the
/// start-up path that was visible: the Dashboard's 300 ms loop, the notification-area tooltip and the Resource
/// History sampler all begin with the same first system snapshot, and they waited behind the slowest provider's
/// first answer — leaving the processor and memory tiles empty, which are plain system calls that need no WMI at
/// all (#2845).</para>
/// <para>One helper rather than the options repeated at twenty call sites: the three settings belong together
/// (<see cref="EnumerationOptions.ReturnImmediately"/> and <see cref="EnumerationOptions.Rewindable"/> are what
/// make the timeout apply to each result rather than to a buffer filled up front), and a copy that drifted would
/// be a search with no limit again. The two places that had already got this right are what this generalises.</para>
/// </remarks>
internal static class Wmi
{
    /// <summary>
    /// How long one read may take before it is given up on.
    /// </summary>
    /// <remarks>
    /// Fifteen seconds is far longer than any answering provider needs — a cold <c>Win32_OperatingSystem</c> read
    /// is milliseconds, the Storage namespace tens of them — and short enough that a user watching an empty tile
    /// sees it fill. The reliability history keeps its own longer limit: it reads a year of records, so a slow
    /// answer there is work rather than a stall.
    /// </remarks>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The bounded options, for a call that takes them itself (<c>GetRelated</c>, for one).</summary>
    internal static EnumerationOptions Options(TimeSpan? timeout = null) => new()
    {
        Timeout = timeout ?? ReadTimeout,
        ReturnImmediately = true,
        Rewindable = false,
    };

    /// <summary>A searcher for <paramref name="query"/> in the default CIM namespace, with a bounded read.</summary>
    internal static ManagementObjectSearcher Searcher(string query, TimeSpan? timeout = null) =>
        Searcher(DefaultScope, query, timeout);

    /// <summary>A searcher for <paramref name="query"/> in <paramref name="scope"/>, with a bounded read.</summary>
    internal static ManagementObjectSearcher Searcher(string scope, string query, TimeSpan? timeout = null) =>
        new(new ManagementScope(scope), new ObjectQuery(query), Options(timeout));

    /// <summary>A searcher in an already-connected <paramref name="scope"/>, with a bounded read.</summary>
    internal static ManagementObjectSearcher Searcher(ManagementScope scope, string query, TimeSpan? timeout = null) =>
        new(scope, new ObjectQuery(query), Options(timeout));

    /// <summary>Where a query with no namespace of its own runs, which is what the parameterless overload used.</summary>
    internal const string DefaultScope = @"\\.\root\cimv2";
}
