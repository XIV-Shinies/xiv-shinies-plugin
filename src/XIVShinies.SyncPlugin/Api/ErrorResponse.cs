using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The error body every non-2xx response shares. Only <see cref="Error"/> is always present; the
/// rest appear for specific statuses, so they are nullable.
/// </summary>
public sealed record ErrorResponse
{
    /// <summary>
    /// A stable machine-readable code: <c>invalid_token</c>, <c>invalid_payload</c>,
    /// <c>payload_too_large</c>, <c>rate_limited</c>, <c>sync_disabled</c>,
    /// <c>tracker_unavailable</c>, or one of the four character refusals a 403 carries
    /// (<c>character_not_claimed</c>, <c>character_not_verified</c>, <c>character_ambiguous</c>,
    /// <c>character_bound_elsewhere</c>).
    /// </summary>
    /// <remarks>
    /// Server-supplied, so it is never displayed or logged. On a 403 it is compared exactly against
    /// the four character-refusal codes (see <see cref="ApiStatusMap.FromHttpStatusCode"/>); on any
    /// other status it is unused.
    /// </remarks>
    public required string Error { get; init; }

    /// <summary>On a 403, the upload's character name echoed back.</summary>
    /// <remarks>
    /// Parsed but not drawn; <c>Windows.CharacterRefusalCopy</c> explains why the window names the
    /// character from the local identity instead.
    /// </remarks>
    public string? Name { get; init; }

    /// <summary>On a 403, the upload's home world echoed back; parsed but not drawn, like
    /// <see cref="Name"/>.</summary>
    public string? World { get; init; }

    /// <summary>On a 400, the flattened validation failures.</summary>
    public ValidationIssues? Issues { get; init; }
}

/// <summary>
/// The flattened validation failures on a 400. A body that was not valid JSON at all arrives in
/// this same shape, with the explanation under <see cref="FormErrors"/>.
/// </summary>
public sealed record ValidationIssues
{
    /// <summary>Errors keyed by the field that failed.</summary>
    // A Dictionary maps keys to values, like a JS object used as a lookup or a `Map`.
    public Dictionary<string, string[]>? FieldErrors { get; init; }

    /// <summary>Errors that apply to the request as a whole rather than one field.</summary>
    public string[]? FormErrors { get; init; }
}
