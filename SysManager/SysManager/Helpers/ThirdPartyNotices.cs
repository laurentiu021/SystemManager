// SysManager · ThirdPartyNotices
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text;

namespace SysManager.Helpers;

/// <summary>
/// The licenses and notices of the open-source components the exe carries, which the build embeds (#2856).
/// </summary>
/// <remarks>
/// <c>THIRD-PARTY-NOTICES.txt</c> at the repository root is written by <c>tools/ThirdPartyNotices.cs</c> from the app's
/// restore graph, and the release writes it again before it publishes, so the exe carries the notices of exactly what
/// it was built from. The licenses of some of those components, LGPL, MPL and Apache among them, ask for their notices
/// and texts to go with every copy, and a copy of the exe is often all a user has: winget installs nothing beside it.
/// About opens them from here.
/// </remarks>
internal static class ThirdPartyNotices
{
    /// <summary>The file's name at the repository root, on each release and where About writes it.</summary>
    internal const string FileName = "THIRD-PARTY-NOTICES.txt";

    /// <summary>The name <c>SysManager.csproj</c> embeds the file under.</summary>
    internal const string ResourceName = "SysManager.THIRD-PARTY-NOTICES.txt";

    /// <summary>The notices this exe carries, or null when it was built without them.</summary>
    internal static string? Read()
    {
        using var stream = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes the notices this exe carries into <paramref name="folder"/>, replacing an earlier copy, and returns the
    /// file's path.
    /// </summary>
    /// <exception cref="InvalidOperationException">The exe was built without them.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder or the file refused the write.</exception>
    internal static string WriteTo(string folder) =>
        WriteTo(folder, Read() ?? throw new InvalidOperationException("This build carries no third-party notices."));

    /// <summary>Writes <paramref name="notices"/> into <paramref name="folder"/> and returns the file's path.</summary>
    internal static string WriteTo(string folder, string notices)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        File.WriteAllText(path, notices, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
