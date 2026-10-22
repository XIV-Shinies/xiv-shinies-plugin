using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The 200 body of <c>GET /api/plugin/v1/me</c>, the status and link probe: the token's account and
/// the characters it has claimed.
/// </summary>
public sealed record MeResponse
{
    /// <summary>The account's claimed characters, ordered alphabetically by name.</summary>
    public required IReadOnlyList<MeCharacter> Characters { get; init; }

    /// <summary>The account the token belongs to.</summary>
    public required MeUser User { get; init; }
}

/// <summary>One character claimed by the token's account.</summary>
public sealed record MeCharacter
{
    /// <summary>
    /// The Lodestone id. A BigInt on the server, so it travels as a string; do not parse it into a
    /// number.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The character's name.</summary>
    public required string Name { get; init; }

    /// <summary>True when a ContentId hash is already bound to this character.</summary>
    public required bool PluginLinked { get; init; }

    /// <summary>
    /// True when the claim is verified by the Lodestone bio code. The server accepts uploads only
    /// for a verified claim, and an upload never verifies one.
    /// </summary>
    public required bool Verified { get; init; }

    /// <summary>The character's home world name.</summary>
    public required string World { get; init; }
}

/// <summary>The account the token belongs to.</summary>
public sealed record MeUser
{
    /// <summary>The account's UUID.</summary>
    public required string Id { get; init; }
}
