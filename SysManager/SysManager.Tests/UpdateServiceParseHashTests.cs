// SysManager · UpdateServiceParseHashTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="UpdateService.ParseExpectedHash"/> — the pure parser that pulls the
/// expected SHA-256 out of a downloaded <c>.sha256</c> file body. Regression guard: an empty or
/// whitespace-only body must degrade to a verification failure (<c>null</c>), never throw. The
/// pre-fix code did <c>Split(' ', RemoveEmptyEntries)[0]</c>, which indexed an empty array and
/// threw <see cref="IndexOutOfRangeException"/> out of the update-integrity path.
/// </summary>
public class UpdateServiceParseHashTests
{
    private const string ValidHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void ParseExpectedHash_BareHash_Returned()
        => Assert.Equal(ValidHash, UpdateService.ParseExpectedHash(ValidHash));

    [Fact]
    public void ParseExpectedHash_HashWithFilename_ReturnsHashOnly()
        => Assert.Equal(ValidHash, UpdateService.ParseExpectedHash($"{ValidHash}  SysManager-v1.2.3.exe"));

    [Fact]
    public void ParseExpectedHash_SurroundingWhitespace_Trimmed()
        => Assert.Equal(ValidHash, UpdateService.ParseExpectedHash($"  {ValidHash}  \r\n"));

    // ── Regression: these previously threw IndexOutOfRangeException ──────────────
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("\t  \t")]
    public void ParseExpectedHash_EmptyOrWhitespaceBody_ReturnsNull(string body)
        => Assert.Null(UpdateService.ParseExpectedHash(body));

    [Fact]
    public void ParseExpectedHash_Null_ReturnsNull()
        => Assert.Null(UpdateService.ParseExpectedHash(null));

    [Fact]
    public void ParseExpectedHash_TooShortToken_ReturnsNull()
        => Assert.Null(UpdateService.ParseExpectedHash("deadbeef"));
}
