// SysManager · ReleaseNoteTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ReleaseNote"/> — the model used in the About tab's release history.
/// </summary>
public class ReleaseNoteTests
{
    [Fact]
    public void Defaults_AreEmpty()
    {
        var n = new ReleaseNote();
        Assert.Equal("", n.Version);
        Assert.Equal("", n.Title);
        Assert.Equal("", n.PublishedAt);
        Assert.Equal("", n.Body);
        Assert.Equal("", n.Url);
        Assert.False(n.IsCurrent);
    }

    [Fact]
    public void AllProperties_Settable()
    {
        var n = new ReleaseNote
        {
            Version = "v0.5.2",
            Title = "Bug fixes",
            PublishedAt = "22 Apr 2026",
            Body = "Fixed crash on startup",
            Url = "https://github.com/laurentiu021/SysManager/releases/tag/v0.5.2",
            IsCurrent = true
        };
        Assert.Equal("v0.5.2", n.Version);
        Assert.Equal("Bug fixes", n.Title);
        Assert.True(n.IsCurrent);
    }
}
