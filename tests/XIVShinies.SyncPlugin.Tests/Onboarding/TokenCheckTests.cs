using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Onboarding;

namespace XIVShinies.SyncPlugin.Tests.Onboarding;

// Turns a GET /me probe's outcome into what the wizard shows. What matters is "your token is wrong"
// (only you can fix it) versus "we could not ask" (try again).
public class TokenCheckTests
{
    [Fact]
    public void A_successful_probe_means_the_token_is_valid()
    {
        Assert.Equal(TokenCheckState.Valid, TokenCheck.FromApiStatus(ApiStatus.Ok));
    }

    // A 401 never heals on retry: the token is unknown, revoked, or malformed.
    [Fact]
    public void A_rejected_token_is_invalid()
    {
        Assert.Equal(TokenCheckState.Invalid, TokenCheck.FromApiStatus(ApiStatus.InvalidToken));
    }

    // A malformed token or a refused backend means nothing was sent, so "could not reach the server"
    // would be wrong.
    [Fact]
    public void A_request_that_was_never_sent_is_invalid_not_unreachable()
    {
        Assert.Equal(TokenCheckState.Invalid, TokenCheck.FromApiStatus(ApiStatus.NotConfigured));
    }

    // None of these says anything about the token; do not blame the user for the server being down.
    [Theory]
    [InlineData(ApiStatus.NetworkError)]
    [InlineData(ApiStatus.ServerError)]
    [InlineData(ApiStatus.SyncDisabled)]
    [InlineData(ApiStatus.RateLimited)]
    [InlineData(ApiStatus.Unknown)]
    [InlineData(ApiStatus.MethodNotAllowed)]
    public void A_failure_that_says_nothing_about_the_token_is_unreachable(ApiStatus status)
    {
        Assert.Equal(TokenCheckState.Unreachable, TokenCheck.FromApiStatus(status));
    }
}
