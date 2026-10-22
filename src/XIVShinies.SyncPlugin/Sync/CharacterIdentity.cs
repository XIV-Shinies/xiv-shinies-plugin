using System;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Who the upload is about: the local character, identified the way the server knows it.
/// </summary>
/// <remarks>
/// Holds the <b>hash</b>, never the raw ContentId (see <see cref="Sync.ContentIdHash"/>). The hash is
/// the durable identity, which lets a character survive a rename or a world transfer. The name and
/// world serve only the server's first-upload binding and naming the character when an upload is
/// refused.
/// </remarks>
public sealed record CharacterIdentity
{
    /// <summary>Lowercase-hex SHA-256 of the character's ContentId.</summary>
    public required string ContentIdHash { get; init; }

    /// <summary>The character's name. The server requires 1-100 characters after trimming.</summary>
    public required string Name { get; init; }

    /// <summary>The character's home world name. The server requires 1-100 characters.</summary>
    public required string HomeWorld { get; init; }

    /// <summary>True when this identity can produce a payload the server will accept.</summary>
    /// <remarks>
    /// The game can say the character is loaded before it is fully populated, and the home world is a
    /// lazy sheet reference that can fail to resolve, so either can be blank. A blank or over-long
    /// value earns a 400, which counts as a plugin bug and does not halt syncing, so a cached bad
    /// identity would quietly re-fail every upload of the session. Real names and worlds are far under
    /// 100 characters; the upper bound keeps the guard matching the contract.
    /// </remarks>
    // Static, so the caller can test the values it read before committing them to a CharacterIdentity.
    public static bool IsUsable(string? name, string? homeWorld) =>
        IsWithinContract(name) && IsWithinContract(homeWorld);

    // The contract's bound: 1-100 characters after trimming (the payload builder trims too).
    private static bool IsWithinContract(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.AsSpan().Trim().Length <= 100;
}
