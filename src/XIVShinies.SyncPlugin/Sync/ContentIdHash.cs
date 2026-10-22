using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Turns the local character's ContentId into the opaque identifier the server knows it by.
/// </summary>
/// <remarks>
/// The raw ContentId <b>never leaves this process</b>: Dalamud asks plugins to hash player identifiers
/// client-side, this project requires it, and only the digest is sent, logged or persisted. The byte
/// order, hash algorithm and casing are <b>permanent</b>, because the server binds a character to this
/// digest on first upload, so a change would get every bound character's uploads refused with a 403.
/// Golden vectors in the tests pin all three; a failure there means the change is wrong.
/// </remarks>
public static class ContentIdHash
{
    /// <summary>Computes the lowercase-hex SHA-256 digest of a ContentId.</summary>
    /// <param name="contentId">The local character's ContentId.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="contentId"/> is zero, the game's "no character loaded" sentinel. Hashing it
    /// would bind an account to a digest that identifies nobody.
    /// </exception>
    public static string Compute(ulong contentId)
    {
        if (contentId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentId), "ContentId is zero; no character is loaded.");
        }

        Span<byte> idBytes = stackalloc byte[sizeof(ulong)];

        // Little-endian and frozen. Explicit rather than BitConverter, so the result cannot change
        // with the machine's endianness.
        BinaryPrimitives.WriteUInt64LittleEndian(idBytes, contentId);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(idBytes, digest);

        // The contract requires lowercase hex: it validates against ^[0-9a-f]{64}$.
        return Convert.ToHexStringLower(digest);
    }
}
