// SysManager · ActivityEntry — a user action logged for the Dashboard history
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

public sealed record ActivityEntry(
    string Action,
    string Detail,
    DateTime Timestamp)
{
    public string TimeAgo
    {
        get
        {
            var elapsed = DateTime.Now - Timestamp;
            if (elapsed.TotalMinutes < 1) return "Just now";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
            if (elapsed.TotalDays < 2) return "Yesterday";
            return $"{(int)elapsed.TotalDays} days ago";
        }
    }
}
