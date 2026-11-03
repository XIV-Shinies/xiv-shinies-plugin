using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XIVShinies.SyncPlugin.Glamour;

/// <summary>
/// Turns one of the local character's retainer ids into the opaque identifier the server stores it
/// by.
/// </summary>
/// <remarks>
/// The raw retainer id <b>never leaves this process</b>; only the digest is sent, as with
/// <see cref="Sync.ContentIdHash"/>. The byte order, hash algorithm and casing are
/// <b>permanent</b>: the server keeps each retainer's last-known name under the digest, so a change
/// would make every retainer look new and lose its name until the player next uses a summoning
/// bell. Golden vectors in the tests pin all three.
/// </remarks>
public static class RetainerIdHash
{
    /// <summary>Computes the lowercase-hex SHA-256 digest of a retainer id.</summary>
    /// <param name="retainerId">The retainer's id. Must not be zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="retainerId"/> is zero, what the game stores in an unused retainer entry. Its
    /// digest would give the server a stable identifier for no retainer at all.
    /// </exception>
    public static string Compute(ulong retainerId)
    {
        if (retainerId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retainerId), "Retainer id is zero; the entry holds no retainer.");
        }

        Span<byte> idBytes = stackalloc byte[sizeof(ulong)];

        // Explicitly little-endian, so the digest cannot change with the machine's byte order.
        BinaryPrimitives.WriteUInt64LittleEndian(idBytes, retainerId);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(idBytes, digest);

        // The contract requires 64 lowercase hex characters.
        return Convert.ToHexStringLower(digest);
    }
}
