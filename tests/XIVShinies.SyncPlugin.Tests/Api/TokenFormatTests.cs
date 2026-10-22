using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// The shape check gives a mistyped or truncated paste a clear message instead of a 401 round trip.
public class TokenFormatTests
{
    // A valid token body: exactly 43 base64url characters.
    private const string ValidBody = "0123456789012345678901234567890123456789abc";

    [Fact]
    public void Accepts_a_correctly_shaped_token()
    {
        Assert.True(TokenFormat.IsWellFormed("xvs_" + ValidBody));
    }

    [Theory]
    [InlineData(null)]                                     // no token configured
    [InlineData("")]                                       // empty
    [InlineData("   ")]                                    // whitespace
    [InlineData("0123456789012345678901234567890123456789abc")] // missing the xvs_ prefix
    [InlineData("xvs_tooshort")]                           // body far too short
    [InlineData("xvs_012345678901234567890123456789012345678")]   // 42-char body (one short)
    [InlineData("xvs_01234567890123456789012345678901234567890abcd")] // too long
    public void Rejects_malformed_tokens(string? token)
    {
        Assert.False(TokenFormat.IsWellFormed(token));
    }

    [Theory]
    [InlineData('+')]  // standard base64, but NOT base64url
    [InlineData('/')]  // ditto
    [InlineData('=')]  // padding is never present
    [InlineData('!')]  // plainly invalid
    public void Rejects_characters_outside_the_base64url_alphabet(char bad)
    {
        var body = ValidBody[..^1] + bad;
        Assert.False(TokenFormat.IsWellFormed("xvs_" + body));
    }
}
