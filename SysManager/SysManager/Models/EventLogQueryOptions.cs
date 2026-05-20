// SysManager · EventLogQueryOptions
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>Filter parameters for one query against one log.</summary>
public sealed class EventLogQueryOptions
{
    public string LogName { get; set; } = "System";
    public List<EventSeverity>? Severities { get; set; }
    public DateTime? Since { get; set; }
    public string? ProviderName { get; set; }
    public int? EventId { get; set; }
    public int MaxResults { get; set; } = 500;
}
