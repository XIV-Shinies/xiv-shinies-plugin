namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The meanings the server contract assigns to each HTTP status the plugin can receive.
/// </summary>
// An `enum` is a fixed set of named values — like a TypeScript string-union type
// (`type ApiStatus = 'Ok' | 'InvalidToken' | ...`), except the values are integers under the
// hood and the compiler enforces exhaustiveness far better than a bare string would.
public enum ApiStatus
{
    /// <summary>200 — the request was applied.</summary>
    Ok,

    /// <summary>400 — the payload failed validation. Our bug; the same body will fail again.</summary>
    InvalidPayload,

    /// <summary>401 — token missing, malformed, or unknown. Never heals on retry.</summary>
    InvalidToken,

    /// <summary>
    /// 403 <c>character_not_claimed</c> — the character is not claimed by this account on the
    /// website. Also the reading of an unrecognized or missing 403 code (see
    /// <see cref="ApiStatusMap.FromHttpStatusCode"/>).
    /// </summary>
    CharacterNotClaimed,

    /// <summary>
    /// 403 <c>character_not_verified</c> — this account has claimed the character, but the claim
    /// is still waiting for its Lodestone bio code. An upload never verifies a claim.
    /// </summary>
    CharacterNotVerified,

    /// <summary>
    /// 403 <c>character_ambiguous</c> — two or more of this account's unbound claims share the
    /// character's name and home world, and the server will not guess which one the upload
    /// belongs to.
    /// </summary>
    CharacterAmbiguous,

    /// <summary>
    /// 403 <c>character_bound_elsewhere</c> — every claim of this account's with the character's
    /// name and home world is already linked to a different game character.
    /// </summary>
    CharacterBoundElsewhere,

    /// <summary>
    /// 405 — wrong HTTP method for the endpoint. Always a bug in this plugin, never something the
    /// user can fix, and repeating the call cannot help.
    /// </summary>
    MethodNotAllowed,

    /// <summary>413 — body over the size cap. Split the upload rather than repeat it.</summary>
    PayloadTooLarge,

    /// <summary>429 — over the per-token rate limit. Honor <c>Retry-After</c>.</summary>
    RateLimited,

    /// <summary>500 — the server failed mid-apply. Writes are idempotent, so a later retry is safe.</summary>
    ServerError,

    /// <summary>
    /// 503 — the server is not taking this upload for now: a switch is off (<c>sync_disabled</c>,
    /// globally or for the user, category or feature), the live tracker is unavailable
    /// (<c>tracker_unavailable</c>), the endpoint is briefly unable to answer, or a proxy or
    /// maintenance page answered for the server. Back off for the advertised window.
    /// </summary>
    SyncDisabled,

    /// <summary>No HTTP response at all (DNS failure, timeout, connection reset).</summary>
    NetworkError,

    /// <summary>
    /// The request was never sent, because the plugin is not usable as configured — no
    /// well-formed token, or a backend URL we refuse to send a token to. Fix the settings; a
    /// retry cannot help.
    /// </summary>
    NotConfigured,

    /// <summary>A status the contract does not define.</summary>
    Unknown,
}

/// <summary>
/// Translates raw HTTP status codes into <see cref="ApiStatus"/> and classifies how the client
/// must react. Keeping this pure (no HttpClient, no Dalamud) is what lets it be unit-tested.
/// </summary>
public static class ApiStatusMap
{
    // The four codes a 403 can carry in its body's `error` field. `const` makes each a
    // compile-time constant, which is what lets them appear as patterns in the switch below.
    private const string NotClaimedCode = "character_not_claimed";
    private const string NotVerifiedCode = "character_not_verified";
    private const string AmbiguousCode = "character_ambiguous";
    private const string BoundElsewhereCode = "character_bound_elsewhere";

    /// <summary>Maps an HTTP status code, and a 403's error code, onto its contract meaning.</summary>
    /// <param name="httpStatusCode">The response's HTTP status.</param>
    /// <param name="errorCode">
    /// The error body's <c>error</c> field, when a body was read. It refines a 403 only: every
    /// other status means what its number means, whatever the body says.
    /// </param>
    // A `switch` expression: each `code => value` arm is tested top to bottom, and `_` is the
    // default (like a `default:` case, or the final `else`). It's an expression, so it *returns*
    // a value rather than executing statements. `string? errorCode = null` is an optional
    // parameter, like a TypeScript `errorCode?: string` that defaults to undefined.
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
            500 => ApiStatus.ServerError,
            503 => ApiStatus.SyncDisabled,
            _ => ApiStatus.Unknown,
        };

    /// <summary>
    /// Which of the four failed character matches a 403 reports. The code is compared exactly,
    /// never parsed or displayed, so a hostile server can only choose among these four readings.
    /// </summary>
    /// <param name="errorCode">The error body's <c>error</c> field, or null when none was read.</param>
    private static ApiStatus FromCharacterRefusalCode(string? errorCode) => errorCode switch
    {
        NotVerifiedCode => ApiStatus.CharacterNotVerified,
        AmbiguousCode => ApiStatus.CharacterAmbiguous,
        BoundElsewhereCode => ApiStatus.CharacterBoundElsewhere,

        // Listed for the reader: the fallback below gives the same answer.
        NotClaimedCode => ApiStatus.CharacterNotClaimed,

        // A code this plugin does not know yet, or a 403 without a readable body. Both still mean
        // the character did not match, and claiming it is the fix that applies to the most players.
        _ => ApiStatus.CharacterNotClaimed,
    };

    /// <summary>
    /// True for the four 403 statuses: the server could not match the upload to a character this
    /// account may write to. Each needs the player to act (or, for a relink, the site's admins);
    /// retrying never helps.
    /// </summary>
    public static bool IsCharacterRefusal(ApiStatus status) =>
        status is ApiStatus.CharacterNotClaimed
            or ApiStatus.CharacterNotVerified
            or ApiStatus.CharacterAmbiguous
            or ApiStatus.CharacterBoundElsewhere;

    /// <summary>
    /// True when repeating the identical request cannot possibly succeed. The caller must stop
    /// and surface the problem to the user rather than retry.
    /// </summary>
    // `is A or B` is pattern matching — a compact way to write "equals any of these". Two different
    // "or"s meet here: `||` joins two whole true/false expressions (as in TypeScript), while the
    // `or` after `is` only joins the values one pattern accepts, so `x || status is A or B` reads
    // `x || (status is A or B)`.
    public static bool IsTerminal(ApiStatus status) =>
        IsCharacterRefusal(status)
            || status is ApiStatus.InvalidToken
            or ApiStatus.InvalidPayload
            or ApiStatus.MethodNotAllowed
            or ApiStatus.PayloadTooLarge
            or ApiStatus.NotConfigured;

    /// <summary>
    /// True when the server explicitly asked us to wait (it sends a <c>Retry-After</c> header).
    /// Not terminal — the same request will succeed later — but never retry immediately.
    /// </summary>
    public static bool ShouldBackOff(ApiStatus status) =>
        status is ApiStatus.RateLimited or ApiStatus.SyncDisabled;

    /// <summary>
    /// True for transient failures where a single retry with backoff is reasonable.
    /// </summary>
    public static bool IsRetryable(ApiStatus status) =>
        status is ApiStatus.ServerError or ApiStatus.NetworkError;
}
