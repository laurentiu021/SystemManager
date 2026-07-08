// SysManager · JsonDefaults
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.Json;

namespace SysManager.Services;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> instances. System.Text.Json caches its
/// serialization metadata per options instance, so reusing one static readonly instance
/// (instead of allocating a fresh options object per call) keeps that cache warm and avoids a
/// per-call allocation. Matches the static-readonly idiom already used by ResourceHistoryService.
/// </summary>
internal static class JsonDefaults
{
    /// <summary>Human-readable, indented output — shared by the settings/snapshot/profile writers.</summary>
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
