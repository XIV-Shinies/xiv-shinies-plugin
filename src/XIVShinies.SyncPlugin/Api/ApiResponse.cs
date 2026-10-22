using System;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The outcome of one API call. Every failure is a value, not an exception, so none can escape into
/// the game.
/// </summary>
/// <typeparam name="T">The success payload type.</typeparam>
public sealed record ApiResponse<T> where T : class
{
    /// <summary>What happened, in the contract's terms.</summary>
    public required ApiStatus Status { get; init; }

    /// <summary>The parsed success body. Non-null only when <see cref="Status"/> is Ok.</summary>
    public T? Value { get; init; }

    /// <summary>The parsed error body, when the server sent one.</summary>
    public ErrorResponse? Error { get; init; }

    /// <summary>
    /// How long the server asked us to wait, from its <c>Retry-After</c> header. Sent with a 429 and
    /// most 503s; a proxy's or a <c>busy</c> 503 may lack it.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// The literal HTTP status code; null when no response arrived. A
    /// <see cref="ApiStatus.NetworkError"/> whose headers arrived before its body failed keeps it.
    /// </summary>
    /// <remarks>
    /// <see cref="Status"/> can erase the number (a 500 and a proxy's 502 are both
    /// <see cref="ApiStatus.ServerError"/>), so diagnostics keep the original.
    /// </remarks>
    public int? HttpStatusCode { get; init; }

    /// <summary>True when the call succeeded and a body was parsed.</summary>
    public bool IsSuccess => Status == ApiStatus.Ok && Value is not null;
}
