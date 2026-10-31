// SysManager · FileDescriptionTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// The description of a running program, which a file renamed or moved while it runs cannot give (#2718).
/// </summary>
/// <remarks>
/// The read threw <see cref="FileNotFoundException"/> for such a file, and the whole process list failed, in Process
/// Manager, the Dashboard's "Why is it slow?" and Volume Control. The reads here are stand-ins.
/// </remarks>
public class FileDescriptionTests
{
    private const string Path = @"C:\Apps\Tool\tool.exe";

    [Fact]
    public void AFileThatIsGone_HasNoDescription()
    {
        Assert.Equal("", FileDescription.Of(Path, path => throw new FileNotFoundException(null, path)));
    }

    [Fact]
    public void AFileOnADriveThatIsGone_HasNoDescription()
    {
        Assert.Equal("", FileDescription.Of(Path, _ => throw new IOException("The device is not ready.")));
    }

    [Fact]
    public void AFileThatIsThere_HasItsDescription()
    {
        Assert.Equal("Tool", FileDescription.Of(Path, _ => "Tool"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void NoPath_HasNoDescription_AndReadsNothing(string path)
    {
        var read = 0;

        Assert.Equal("", FileDescription.Of(path, _ => { read++; return "Tool"; }));
        Assert.Equal(0, read);
    }
}
