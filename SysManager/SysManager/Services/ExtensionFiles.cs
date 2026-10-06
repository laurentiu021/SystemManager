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
/// <remarks>
/// Every value the readers take from that JSON comes through here, read by its kind. The parser accepts things a
/// read then refuses with an exception — bytes that are not UTF-8 inside a string, an escaped half of a surrogate
/// pair in a key, which makes even a lookup of another key in the same object throw — and another program can
/// write anything into these files. Read here, each such value is simply absent, and costs nothing else.
/// <c>ArchitectureTests.TheExtensionReaders_ReadTheirJsonOnlyThroughExtensionFiles</c> keeps the readers to that.
/// </remarks>
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
    /// comments (Chromium allows them in a manifest), trailing commas, and nesting as deep as Chromium's own reader
    /// takes. Null when it is not JSON.
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
                MaxDepth = 200,
            });
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The value of the property <paramref name="name"/> on an object, found by reading each property's name in turn,
    /// the last of several winning, as the parser's own lookup does. Unlike that lookup it never throws: a key that
    /// cannot be read is just not the one asked for.
    /// </summary>
    internal static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var (key, item) in Properties(element))
        {
            if (!string.Equals(key, name, StringComparison.Ordinal)) continue;
            value = item;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// An object's properties, each with its name, or a null name when the name cannot be read; nothing for anything
    /// but an object.
    /// </summary>
    internal static IEnumerable<(string? Name, JsonElement Value)> Properties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in element.EnumerateObject())
            yield return (NameOf(property), property.Value);
    }

    private static string? NameOf(JsonProperty property)
    {
        try { return property.Name; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The string at <paramref name="name"/> on an object, or null — also when it is not valid text.</summary>
    internal static string? String(JsonElement element, string name) =>
        TryGet(element, name, out var value) ? Text(value) : null;

    /// <summary>The element as a string, or null when it is not one or is not valid text.</summary>
    internal static string? Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        try { return value.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// The whole number at <paramref name="name"/> on an object, or null when it is absent, not a number, or does
    /// not fit: reading a number from anything else throws.
    /// </summary>
    internal static long? Number(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>The number at <paramref name="name"/> as an <see cref="int"/>, or null (see <see cref="Number"/>).</summary>
    internal static int? Int(JsonElement element, string name) =>
        Number(element, name) is { } number && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;

    /// <summary>The boolean at <paramref name="name"/> on an object, or null when it is absent or not a boolean.</summary>
    internal static bool? Bool(JsonElement element, string name) =>
        TryGet(element, name, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    /// <summary>True when the object has a property <paramref name="name"/> whose value is anything but null.</summary>
    internal static bool Has(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>The strings in the array at <paramref name="name"/> on an object; empty when there is none.</summary>
    internal static IEnumerable<string> Strings(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var array) || array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in array.EnumerateArray())
        {
            if (Text(item) is { Length: > 0 } text) yield return text;
        }
    }

    /// <summary>
    /// The icon to show from an <c>icons</c> object of size → path: the smallest at least 32 pixels, or else the
    /// largest. Null when there is none.
    /// </summary>
    internal static string? PickIcon(JsonElement icons)
    {
        List<(int Size, string Path)> candidates = [];
        foreach (var (key, value) in Properties(icons))
        {
            if (int.TryParse(key, out var size) && size > 0 && Text(value) is { Length: > 0 } path)
                candidates.Add((size, path));
        }
        if (candidates.Count == 0) return null;
        var bigEnough = candidates.Where(c => c.Size >= 32).OrderBy(c => c.Size).ToList();
        return bigEnough.Count > 0 ? bigEnough[0].Path : candidates.MaxBy(c => c.Size).Path;
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/>, compared as full paths.</summary>
    internal static bool IsUnder(string path, string folder)
    {
        try
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    /// <summary>
    /// <paramref name="relative"/> resolved under <paramref name="folder"/>, or null when it would leave it — a
    /// drive or other root, a path that climbs out with <c>..</c>, or one that passes through anything but a plain
    /// folder, such as a link, which could lead anywhere. An extension names its own files, and nothing outside its
    /// folder is read on its say-so. A leading slash is allowed, as browsers read it from the extension's own root.
    /// </summary>
    internal static string? Inside(string folder, string relative)
    {
        var trimmed = (relative ?? "").Replace('\\', '/').TrimStart('/');
        if (trimmed.Length == 0 || trimmed.Contains(':', StringComparison.Ordinal) || Path.IsPathRooted(trimmed)
            || HasAPartWindowsRenames(trimmed))
        {
            return null;
        }
        try
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, trimmed));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

            // Each folder between the extension's own and the file, from the top, looked at without following it. The
            // first one that does not exist ends the walk: nothing below it exists either, and a long made-up path
            // then costs one look rather than one per level.
            var parts = full[root.Length..].Split(Path.DirectorySeparatorChar);
            var current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                current = Path.Combine(current, parts[i]);
                var kind = KindOf(current);
                if (kind is EntryKind.Missing) break;
                if (kind is not EntryKind.Folder) return null;
            }
            return full;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }

    /// <summary>
    /// True when a part of the path ends in a space or a dot. Windows drops those from the last part of a path but not
    /// from one in the middle, so such a folder is looked at under one name and read through under another: a check
    /// would pass on a plain <c>img</c> while the read goes through a link named <c>img </c>.
    /// </summary>
    internal static bool HasAPartWindowsRenames(string path) =>
        path.Split('\\', '/').Any(part => part.Length > 0 && part != "." && part != ".." && part[^1] is ' ' or '.');

    /// <summary>What is at a path, as <see cref="KindOf"/> sees it.</summary>
    internal enum EntryKind
    {
        /// <summary>Nothing: the path, or a folder above it, does not exist.</summary>
        Missing,

        /// <summary>A folder.</summary>
        Folder,

        /// <summary>A file.</summary>
        File,

        /// <summary>
        /// A junction, a symbolic link or another reparse point, such as a cloud-storage placeholder: never followed.
        /// </summary>
        Link,

        /// <summary>Something whose attributes could not be read.</summary>
        Unreadable,
    }

    /// <summary>
    /// What is at <paramref name="path"/>, looked at without following it: a link is reported as a link, even one
    /// whose target is gone, so a caller can say it was not followed rather than that nothing is there.
    /// </summary>
    internal static EntryKind KindOf(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return EntryKind.Link;
            return (attributes & FileAttributes.Directory) != 0 ? EntryKind.Folder : EntryKind.File;
        }
        catch (FileNotFoundException) { return EntryKind.Missing; }
        catch (DirectoryNotFoundException) { return EntryKind.Missing; }
        catch (IOException) { return EntryKind.Unreadable; }
        catch (UnauthorizedAccessException) { return EntryKind.Unreadable; }
        catch (ArgumentException) { return EntryKind.Unreadable; }
        catch (NotSupportedException) { return EntryKind.Unreadable; }
    }

    /// <summary>
    /// True for a full path on one of this PC's own drives (<c>C:\…</c>). A browser's files can name a network share,
    /// a mapped network drive or a device path (<c>\\server\share</c>, <c>\\?\…</c>); such a path is never opened,
    /// since reading it would reach another machine, with the user's credentials, during what is only a look.
    /// </summary>
    internal static bool IsOnALocalDrive(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return false;
        try { return new DriveInfo(path[..1]).DriveType != DriveType.Network; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>True when <paramref name="host"/> is <paramref name="domain"/> itself or one of its subdomains.</summary>
    internal static bool IsHostOf(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
