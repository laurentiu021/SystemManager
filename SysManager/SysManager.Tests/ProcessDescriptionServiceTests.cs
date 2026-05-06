// SysManager · ProcessDescriptionServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;
using Xunit;

namespace SysManager.Tests;

public class ProcessDescriptionServiceTests
{
    private ProcessDescriptionService Sut => ProcessDescriptionService.Instance;

    [Fact]
    public void Database_LoadsSuccessfully()
    {
        Assert.True(Sut.Count > 0);
    }

    [Theory]
    [InlineData("svchost", "System")]
    [InlineData("chrome", "Browser")]
    [InlineData("Code", "Development")]
    [InlineData("Discord", "Communication")]
    [InlineData("Steam", "Gaming")]
    [InlineData("Spotify", "Media")]
    public void GetCategory_ReturnsCorrectCategory(string processName, string expectedCategory)
    {
        Assert.Equal(expectedCategory, Sut.GetCategory(processName));
    }

    [Theory]
    [InlineData("svchost", ProcessSafety.System)]
    [InlineData("chrome", ProcessSafety.Trusted)]
    [InlineData("lsass", ProcessSafety.System)]
    public void GetSafety_ReturnsCorrectLevel(string processName, ProcessSafety expected)
    {
        Assert.Equal(expected, Sut.GetSafety(processName));
    }

    [Fact]
    public void GetSafety_UnknownProcess_ReturnsUnknown()
    {
        Assert.Equal(ProcessSafety.Unknown, Sut.GetSafety("totally_random_process_xyz"));
    }

    [Fact]
    public void GetDescription_KnownProcess_ReturnsNonEmpty()
    {
        var desc = Sut.GetDescription("explorer");
        Assert.False(string.IsNullOrWhiteSpace(desc));
        Assert.Contains("desktop", desc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDescription_UnknownProcess_ReturnsEmpty()
    {
        Assert.Equal("", Sut.GetDescription("nonexistent_process_abc"));
    }

    [Fact]
    public void Lookup_CaseInsensitive()
    {
        var lower = Sut.Lookup("svchost");
        var upper = Sut.Lookup("SVCHOST");
        var mixed = Sut.Lookup("SvcHost");

        Assert.NotNull(lower);
        Assert.NotNull(upper);
        Assert.NotNull(mixed);
        Assert.Equal(lower!.Description, upper!.Description);
        Assert.Equal(lower.Description, mixed!.Description);
    }

    [Fact]
    public void Lookup_StripsExeExtension()
    {
        var withExt = Sut.Lookup("svchost.exe");
        var without = Sut.Lookup("svchost");

        Assert.NotNull(withExt);
        Assert.NotNull(without);
        Assert.Equal(withExt!.Description, without!.Description);
    }

    [Fact]
    public void Lookup_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(Sut.Lookup(null!));
        Assert.Null(Sut.Lookup(""));
        Assert.Null(Sut.Lookup("   "));
    }

    [Fact]
    public void GetCategories_ReturnsMultiple()
    {
        var categories = Sut.GetCategories();
        Assert.True(categories.Count >= 5);
        Assert.Contains("System", categories);
        Assert.Contains("Browser", categories);
    }
}
