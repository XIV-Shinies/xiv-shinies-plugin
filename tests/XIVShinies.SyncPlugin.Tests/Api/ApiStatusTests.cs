using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// The status map keeps the plugin from hammering a server that told it to stop: a 401 never heals,
// and 429 and 503 mean back off.
public class ApiStatusTests
{
    [Theory]
    [InlineData(200, ApiStatus.Ok)]
    [InlineData(400, ApiStatus.InvalidPayload)]
    [InlineData(401, ApiStatus.InvalidToken)]
    [InlineData(403, ApiStatus.CharacterNotClaimed)]
    [InlineData(405, ApiStatus.MethodNotAllowed)]
    [InlineData(413, ApiStatus.PayloadTooLarge)]
    [InlineData(429, ApiStatus.RateLimited)]
    [InlineData(500, ApiStatus.ServerError)]
    [InlineData(503, ApiStatus.SyncDisabled)]
    [InlineData(418, ApiStatus.Unknown)]  // any other code outside 5xx
    public void Maps_http_status_codes_to_contract_meanings(int httpStatus, ApiStatus expected)
    {
        Assert.Equal(expected, ApiStatusMap.FromHttpStatusCode(httpStatus));
    }

    // Every 5xx but 503 is a server error, including the codes a proxy sends when it cannot reach the
    // server. ApiStatus.ServerError lists them and says why a retry is safe.
    [Theory]
    [InlineData(501)]
    [InlineData(502)]
    [InlineData(504)]
    [InlineData(520)]
    [InlineData(530)]
    [InlineData(599)]
    public void Every_other_server_side_failure_reads_as_a_server_error(int httpStatus)
    {
        Assert.Equal(ApiStatus.ServerError, ApiStatusMap.FromHttpStatusCode(httpStatus));
    }

    [Theory]
    [InlineData(499)]
    [InlineData(600)]
    public void A_code_outside_the_5xx_range_is_unknown(int httpStatus)
    {
        Assert.Equal(ApiStatus.Unknown, ApiStatusMap.FromHttpStatusCode(httpStatus));
    }

    // A 403 is always a failed character match, and its error code names which of four; each needs a
    // different fix. ApiStatusMap.FromCharacterRefusalCode says why an unknown or missing code falls
    // back to not-claimed.
    [Theory]
    [InlineData("character_not_claimed", ApiStatus.CharacterNotClaimed)]
    [InlineData("character_not_verified", ApiStatus.CharacterNotVerified)]
    [InlineData("character_ambiguous", ApiStatus.CharacterAmbiguous)]
    [InlineData("character_bound_elsewhere", ApiStatus.CharacterBoundElsewhere)]
    [InlineData("character_something_new", ApiStatus.CharacterNotClaimed)]
    [InlineData("", ApiStatus.CharacterNotClaimed)]
    [InlineData(null, ApiStatus.CharacterNotClaimed)]
    public void Maps_each_403_error_code_to_its_own_status(string? errorCode, ApiStatus expected)
    {
        Assert.Equal(expected, ApiStatusMap.FromHttpStatusCode(403, errorCode));
    }

    // Codes are compared exactly, and the server sends them lowercase, so a near miss is an unknown
    // code and takes the fallback.
    [Fact]
    public void A_403_error_code_that_differs_only_in_case_takes_the_fallback()
    {
        Assert.Equal(
            ApiStatus.CharacterNotClaimed,
            ApiStatusMap.FromHttpStatusCode(403, "CHARACTER_NOT_VERIFIED"));
    }

    [Theory]
    [InlineData(401, ApiStatus.InvalidToken)]
    [InlineData(429, ApiStatus.RateLimited)]
    [InlineData(200, ApiStatus.Ok)]
    public void An_error_code_never_changes_a_status_other_than_403(int httpStatus, ApiStatus expected)
    {
        Assert.Equal(expected, ApiStatusMap.FromHttpStatusCode(httpStatus, "character_not_verified"));
    }

    [Theory]
    [InlineData(ApiStatus.CharacterNotClaimed, true)]
    [InlineData(ApiStatus.CharacterNotVerified, true)]
    [InlineData(ApiStatus.CharacterAmbiguous, true)]
    [InlineData(ApiStatus.CharacterBoundElsewhere, true)]
    [InlineData(ApiStatus.InvalidToken, false)]
    [InlineData(ApiStatus.NotConfigured, false)]
    [InlineData(ApiStatus.Ok, false)]
    public void Character_refusals_are_exactly_the_four_403_statuses(ApiStatus status, bool expected)
    {
        Assert.Equal(expected, ApiStatusMap.IsCharacterRefusal(status));
    }

    // Terminal: retrying the same request cannot help, so stop and show the user.
    [Theory]
    [InlineData(ApiStatus.InvalidToken)]             // 401 never heals on retry
    [InlineData(ApiStatus.CharacterNotClaimed)]      // user must claim the character on the website
    [InlineData(ApiStatus.CharacterNotVerified)]     // user must finish the Lodestone bio check
    [InlineData(ApiStatus.CharacterAmbiguous)]       // user must remove a duplicate claim
    [InlineData(ApiStatus.CharacterBoundElsewhere)]  // only a relink by the site's admins helps
    [InlineData(ApiStatus.InvalidPayload)]           // our bug — the same body will fail again
    [InlineData(ApiStatus.PayloadTooLarge)]          // must split the upload, not repeat it
    [InlineData(ApiStatus.MethodNotAllowed)]         // a bug in this plugin; the call is wrong
    [InlineData(ApiStatus.NotConfigured)]            // never even sent; fix the settings first
    public void Terminal_statuses_must_not_be_retried(ApiStatus status)
    {
        Assert.True(ApiStatusMap.IsTerminal(status));
        Assert.False(ApiStatusMap.IsRetryable(status));
    }

    // The client produces NotConfigured before any request is sent, so no HTTP status may map to it.
    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(503)]
    [InlineData(418)]
    public void NotConfigured_is_never_produced_from_an_http_status(int httpStatus)
    {
        Assert.NotEqual(ApiStatus.NotConfigured, ApiStatusMap.FromHttpStatusCode(httpStatus));
    }

    // Back off: the server said to wait, and sends Retry-After.
    [Theory]
    [InlineData(ApiStatus.RateLimited)]
    [InlineData(ApiStatus.SyncDisabled)]
    public void Backoff_statuses_are_not_terminal_and_not_immediately_retryable(ApiStatus status)
    {
        Assert.True(ApiStatusMap.ShouldBackOff(status));
        Assert.False(ApiStatusMap.IsTerminal(status));
        Assert.False(ApiStatusMap.IsRetryable(status));
    }

    // Transient: retried once, with backoff.
    [Theory]
    [InlineData(ApiStatus.ServerError)]
    [InlineData(ApiStatus.NetworkError)]
    public void Transient_failures_are_retryable(ApiStatus status)
    {
        Assert.True(ApiStatusMap.IsRetryable(status));
        Assert.False(ApiStatusMap.IsTerminal(status));
    }

    [Fact]
    public void Ok_is_neither_terminal_nor_retryable_nor_backoff()
    {
        Assert.False(ApiStatusMap.IsTerminal(ApiStatus.Ok));
        Assert.False(ApiStatusMap.IsRetryable(ApiStatus.Ok));
        Assert.False(ApiStatusMap.ShouldBackOff(ApiStatus.Ok));
    }
}
