// SysManager · ExtensionFiles
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// The two ways the extension readers touch a browser's files (#1526): a bounded, shared read of one file, and a
/// tolerant parse of the JSON in it. Read-only throughout, and shared so a running browser keeps its files.
/// </summary>
internal static class ExtensionFiles
{
    /// <summary>A manifest or a translation file. Real ones are a few kilobytes.</summary>
    internal const int MaxManifestBytes = 1 << 20;

    /// <summary>An icon. Real ones are a few kilobytes; the list draws them at 28 pixels.</summary>
    internal const int MaxIconBytes = 512 << 10;

    /// <summary>A browser's settings file or Firefox's extension list. Real ones are under half a megabyte.</summary>
    internal const int MaxSettingsBytes = 32 << 20;

    /// <summary>
    /// The file's bytes, or null when it is missing, larger than <paramref name="maxBytes"/>, a link, or cannot be
    /// read. Opened for reading with every share mode, so a browser that has it open is not disturbed.
    /// </summary>
    internal static byte[]? Read(string path, int maxBytes)
    {
        try
        {
            if (!File.Exists(path) || SafeFileWalk.IsReparsePoint(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Parses <paramref name="bytes"/> as JSON the way browsers write and accept it: an optional byte-order mark,
    /// comments (Chromium allows them in a manifest) and trailing commas. Null when it is not JSON.
    /// </summary>
    internal static JsonDocument? Parse(byte[]? bytes)
    {
        if (bytes is null) return null;
        ReadOnlyMemory<byte> json = bytes;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) json = json[3..];
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 128,
            });
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The string at <paramref name="name"/> on an object, or null.</summary>
    internal static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The boolean at <paramref name="name"/> on an object, or null when it is absent or not a boolean.</summary>
    internal static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    /// <summary>The strings in the array at <paramref name="name"/> on an object; empty when there is none.</summary>
    internal static IEnumerable<string> Strings(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) yield return text;
        }
    }

    /// <summary>
    /// The icon to show from an <c>icons</c> object of size → path: the smallest at least 32 pixels, or else the
    /// largest. Null when there is none.
    /// </summary>
    internal static string? PickIcon(JsonElement icons)
    {
        if (icons.ValueKind != JsonValueKind.Object) return null;
        List<(int Size, string Path)> candidates = [];
        foreach (var property in icons.EnumerateObject())
        {
            if (int.TryParse(property.Name, out var size) && size > 0
                && property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() is { Length: > 0 } path)
            {
                candidates.Add((size, path));
            }
        }
        if (candidates.Count == 0) return null;
        var bigEnough = candidates.Where(c => c.Size >= 32).OrderBy(c => c.Size).ToList();
        return bigEnough.Count > 0 ? bigEnough[0].Path : candidates.MaxBy(c => c.Size).Path;
    }

    /// <summary>
    /// <paramref name="relative"/> resolved under <paramref name="folder"/>, or null when it would leave it — a
    /// drive or other root, or a path that climbs out with <c>..</c>. An extension names its own files, and nothing
    /// outside its folder is read on its say-so. A leading slash is allowed, as browsers read it from the
    /// extension's own root.
    /// </summary>
    internal static string? Inside(string folder, string relative)
    {
        var trimmed = (relative ?? "").Replace('\\', '/').TrimStart('/');
        if (trimmed.Length == 0 || trimmed.Contains(':', StringComparison.Ordinal) || Path.IsPathRooted(trimmed))
            return null;
        try
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, trimmed));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }
}
