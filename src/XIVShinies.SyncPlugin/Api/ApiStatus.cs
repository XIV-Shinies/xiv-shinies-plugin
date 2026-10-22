namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The meanings the server contract assigns to each HTTP status the plugin can receive.
/// </summary>
public enum ApiStatus
{
    /// <summary>200: the request was applied.</summary>
    Ok,

    /// <summary>400: the payload failed validation. Our bug; the same body will fail again.</summary>
    InvalidPayload,

    /// <summary>401: token missing, malformed, or unknown. Never heals on retry.</summary>
    InvalidToken,

    /// <summary>
    /// 403 <c>character_not_claimed</c>: this account has not claimed the character on the website.
    /// Also how an unknown or missing 403 code reads (see <see cref="ApiStatusMap.FromHttpStatusCode"/>).
    /// </summary>
    CharacterNotClaimed,

    /// <summary>
    /// 403 <c>character_not_verified</c>: this account's claim on the character still waits for its
    /// Lodestone bio code. An upload never verifies a claim.
    /// </summary>
    CharacterNotVerified,

    /// <summary>
    /// 403 <c>character_ambiguous</c>: two or more of this account's unbound claims share the
    /// character's name and home world, and the server will not guess which one the upload is for.
    /// </summary>
    CharacterAmbiguous,

    /// <summary>
    /// 403 <c>character_bound_elsewhere</c>: every claim of this account's with the character's name
    /// and home world is already linked to a different game character.
    /// </summary>
    CharacterBoundElsewhere,

    /// <summary>405: wrong HTTP method for the endpoint. Always a plugin bug; a retry cannot help.</summary>
    MethodNotAllowed,

    /// <summary>413: body over the size cap. Split the upload rather than repeat it.</summary>
    PayloadTooLarge,

    /// <summary>429: over the per-token rate limit. Honor <c>Retry-After</c>.</summary>
    RateLimited,

    /// <summary>
    /// 500 when the server failed mid-apply, or any 5xx but 503, such as the 502 or 504 a proxy sends
    /// when it cannot reach or hear back from the server. Writes are idempotent, so a later retry is safe.
    /// </summary>
    ServerError,

    /// <summary>
    /// 503: the server is not taking this upload for now. Back off for the advertised window.
    /// </summary>
    /// <remarks>
    /// A switch is off (<c>sync_disabled</c>, globally or for the user, category or feature), the live
    /// tracker is unavailable (<c>tracker_unavailable</c>), the endpoint briefly cannot answer, or a
    /// proxy or maintenance page answered instead.
    /// </remarks>
    SyncDisabled,

    /// <summary>
    /// No usable response: none arrived (DNS failure, timeout, connection reset), or its body was
    /// refused as too large or cut off by a dropped connection, the deadline, or unload.
    /// </summary>
    NetworkError,

    /// <summary>
    /// Never sent: there is no well-formed token, or the backend URL is one the plugin will not send
    /// a token to. Fix the settings; a retry cannot help.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// A status outside the 5xx range that the contract does not define, such as a 404, or a 200
    /// whose body was empty or did not parse.
    /// </summary>
    Unknown,
}

/// <summary>
/// Translates raw HTTP status codes into <see cref="ApiStatus"/> and classifies how the client must
/// react.
/// </summary>
public static class ApiStatusMap
{
    // The four codes a 403 can carry in its body's `error` field.
    private const string NotClaimedCode = "character_not_claimed";
    private const string NotVerifiedCode = "character_not_verified";
    private const string AmbiguousCode = "character_ambiguous";
    private const string BoundElsewhereCode = "character_bound_elsewhere";

    /// <summary>Maps an HTTP status code, and a 403's error code, onto its contract meaning.</summary>
    /// <param name="httpStatusCode">The response's HTTP status.</param>
    /// <param name="errorCode">
    /// The error body's <c>error</c> field, when one was read. It refines a 403 only; every other
    /// status means what its number means.
    /// </param>
    public static ApiStatus FromHttpStatusCode(int httpStatusCode, string? errorCode = null) =>
        httpStatusCode switch
        {
            200 => ApiStatus.Ok,
            400 => ApiStatus.InvalidPayload,
            401 => ApiStatus.InvalidToken,
            403 => FromCharacterRefusalCode(errorCode),
            405 => ApiStatus.MethodNotAllowed,
            413 => ApiStatus.PayloadTooLarge,
            429 => ApiStatus.RateLimited,
            // Redundant with the range below; listed for the reader.
            500 => ApiStatus.ServerError,
            503 => ApiStatus.SyncDisabled,

            // The arms above match first, so a 503 never reaches this one.
            >= 500 and < 600 => ApiStatus.ServerError,
            _ => ApiStatus.Unknown,
        };

    /// <summary>
    /// Which of the four failed character matches a 403 reports. The code is compared exactly, never
    /// parsed or displayed, so a hostile server can only choose among these four readings.
    /// </summary>
    /// <param name="errorCode">The error body's <c>error</c> field, or null when none was read.</param>
    private static ApiStatus FromCharacterRefusalCode(string? errorCode) => errorCode switch
    {
        NotVerifiedCode => ApiStatus.CharacterNotVerified,
        AmbiguousCode => ApiStatus.CharacterAmbiguous,
        BoundElsewhereCode => ApiStatus.CharacterBoundElsewhere,

        // Redundant with the fallback below; listed for the reader.
        NotClaimedCode => ApiStatus.CharacterNotClaimed,

        // An unknown code, or a 403 without a readable body, still means no match, and claiming the
        // character is the fix that applies to the most players.
        _ => ApiStatus.CharacterNotClaimed,
    };

    /// <summary>
    /// True for the four 403 statuses: the server could not match the upload to a character this
    /// account may write to. Each needs the player, or for a relink the site's admins, to act.
    /// </summary>
    public static bool IsCharacterRefusal(ApiStatus status) =>
        status is ApiStatus.CharacterNotClaimed
            or ApiStatus.CharacterNotVerified
            or ApiStatus.CharacterAmbiguous
            or ApiStatus.CharacterBoundElsewhere;

    /// <summary>
    /// True when repeating the identical request cannot succeed. Stop and tell the user rather than
    /// retry.
    /// </summary>
    public static bool IsTerminal(ApiStatus status) =>
        IsCharacterRefusal(status)
            || status is ApiStatus.InvalidToken
            or ApiStatus.InvalidPayload
            or ApiStatus.MethodNotAllowed
            or ApiStatus.PayloadTooLarge
            or ApiStatus.NotConfigured;

    /// <summary>
    /// True when the server asked us to wait with a <c>Retry-After</c> header. The same request will
    /// succeed later, but never retry at once.
    /// </summary>
    public static bool ShouldBackOff(ApiStatus status) =>
        status is ApiStatus.RateLimited or ApiStatus.SyncDisabled;

    /// <summary>True for transient failures where a single retry with backoff is reasonable.</summary>
    public static bool IsRetryable(ApiStatus status) =>
        status is ApiStatus.ServerError or ApiStatus.NetworkError;
}
