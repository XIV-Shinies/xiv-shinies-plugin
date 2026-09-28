using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// Maps raw HTTP status codes onto the meanings the server contract assigns them, and classifies
// how the client must react. Getting this table right is what keeps the plugin from hammering a
// server that told it to stop (401 never heals; 429/503 mean back off).
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
    [InlineData(418, ApiStatus.Unknown)]  // anything the contract doesn't define
    public void Maps_http_status_codes_to_contract_meanings(int httpStatus, ApiStatus expected)
    {
        Assert.Equal(expected, ApiStatusMap.FromHttpStatusCode(httpStatus));
    }

    // A 403 is always a failed character match, and the body's error code says which of four it
    // is. Each needs a different fix, so each gets its own status. An unknown or missing code takes
    // the not-claimed fallback (ApiStatusMap.FromCharacterRefusalCode says why).
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

    // Codes match exactly. The server sends them lowercase, and a near miss is a code this plugin
    // does not know, which takes the fallback above rather than a guess.
    [Fact]
    public void A_403_error_code_that_differs_only_in_case_takes_the_fallback()
    {
        Assert.Equal(
            ApiStatus.CharacterNotClaimed,
            ApiStatusMap.FromHttpStatusCode(403, "CHARACTER_NOT_VERIFIED"));
    }

    // The error code refines a 403 only. Every other status means what its number means, whatever
    // the body says.
    [Theory]
    [InlineData(401, ApiStatus.InvalidToken)]
    [InlineData(429, ApiStatus.RateLimited)]
    [InlineData(200, ApiStatus.Ok)]
    public void An_error_code_never_changes_a_status_other_than_403(int httpStatus, ApiStatus expected)
    {
        Assert.Equal(expected, ApiStatusMap.FromHttpStatusCode(httpStatus, "character_not_verified"));
    }

    // The four character refusals are one family: the helper that names the family must cover
    // every member and nothing else.
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

    // "Terminal" = retrying the identical request cannot help; stop and surface it to the user.
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

    // NotConfigured is produced by the client before any request leaves the machine, so no HTTP
    // status code may ever map onto it.
    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(503)]
    [InlineData(418)]
    public void NotConfigured_is_never_produced_from_an_http_status(int httpStatus)
    {
        Assert.NotEqual(ApiStatus.NotConfigured, ApiStatusMap.FromHttpStatusCode(httpStatus));
    }

    // "Back off" = the server explicitly told us to wait (it sends Retry-After).
    [Theory]
    [InlineData(ApiStatus.RateLimited)]
    [InlineData(ApiStatus.SyncDisabled)]
    public void Backoff_statuses_are_not_terminal_and_not_immediately_retryable(ApiStatus status)
    {
        Assert.True(ApiStatusMap.ShouldBackOff(status));
        Assert.False(ApiStatusMap.IsTerminal(status));
        Assert.False(ApiStatusMap.IsRetryable(status));
    }

    // Transient failures we may retry once with backoff.
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
