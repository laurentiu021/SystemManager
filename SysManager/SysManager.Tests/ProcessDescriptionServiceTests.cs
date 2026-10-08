// SysManager · ProcessDescriptionServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
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

    // ---------- the program's own name (#1529) ----------

    [Fact]
    public void ShortName_IsTheNameBeforeTheDash()
    {
        Assert.Equal("Google Chrome", Sut.Lookup("chrome")!.ShortName);
        Assert.Equal("Microsoft Defender Antimalware", Sut.Lookup("MsMpEng")!.ShortName);
    }

    [Theory]
    [InlineData("A tool with no dash in its description", "A tool with no dash in its description")]
    [InlineData(" — a dash with no name before it", " — a dash with no name before it")]
    [InlineData("", "")]
    public void ShortName_WithNoNameToEndAtADash_IsTheWholeDescription(string description, string expected)
    {
        Assert.Equal(expected, new ProcessDescriptionEntry("x", description, "Utility", ProcessSafety.Trusted).ShortName);
    }

    /// <summary>
    /// Every entry names its program before a spaced em dash, so <see cref="ProcessDescriptionEntry.ShortName"/> is a name
    /// for all of them rather than half a sentence for some.
    /// </summary>
    /// <remarks>
    /// The "Why is it slow?" check names the program using the processor by it ("Google Chrome is using 61% of the
    /// processor"). An entry written another way would put its whole description into that sentence. Read from the
    /// file the build embeds, so an entry added in another shape fails here rather than on someone's Dashboard.
    /// </remarks>
    [Fact]
    public void EveryEntry_StartsWithTheProgramsName()
    {
        using var json = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(TestPaths.AppFile("Data", "ProcessDescriptions.json")));
        var entries = json.RootElement.EnumerateArray().ToList();

        // A floor, so a file this reads wrongly cannot pass by having nothing in it.
        Assert.True(entries.Count >= 100, $"only {entries.Count} entries were read from the database.");

        var unnamed = entries
            .Select(e => (Name: e.GetProperty("name").GetString() ?? "", Description: e.GetProperty("description").GetString() ?? ""))
            .Where(e => e.Description.IndexOf(" — ", StringComparison.Ordinal) <= 0)
            .Select(e => $"{e.Name}: \"{e.Description}\"")
            .ToList();
        Assert.True(unnamed.Count == 0,
            "these entries do not start with the program's name and a spaced em dash, so the slowness check would "
            + "name them by their whole description:\n  " + string.Join("\n  ", unnamed));
    }
}
