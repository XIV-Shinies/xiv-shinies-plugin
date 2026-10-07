using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XIVShinies.SyncPlugin.Glamour;

/// <summary>
/// Turns one of the local character's retainer ids into the opaque identifier the server stores
/// it by.
/// </summary>
/// <remarks>
/// <para>
/// The raw retainer id <b>never leaves this process</b>, the same rule the character's own id
/// follows (see <see cref="Sync.ContentIdHash"/>): only the digest is sent.
/// </para>
/// <para>
/// <b>The byte representation is permanent.</b> The server keeps each retainer's last-known name
/// under this digest, so a change to the byte order, the algorithm or the casing would make every
/// retainer look new and lose its name until the player next uses a summoning bell. Golden vectors
/// in the tests pin all three. It uses the same representation as the character id: SHA-256 of the
/// id's eight bytes, little-endian, in lowercase hex.
/// </para>
/// </remarks>
public static class RetainerIdHash
{
    /// <summary>Computes the lowercase-hex SHA-256 digest of a retainer id.</summary>
    /// <param name="retainerId">The retainer's id. Must not be zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="retainerId"/> is zero, what the game stores in an unused retainer entry.
    /// Hashing it would hand the server a stable identifier for no retainer at all.
    /// </exception>
    public static string Compute(ulong retainerId)
    {
        if (retainerId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retainerId), "Retainer id is zero; the entry holds no retainer.");
        }

        // `stackalloc` puts these bytes on the stack rather than the heap: no allocation, and they
        // vanish when the method returns. There is no JS equivalent.
        Span<byte> idBytes = stackalloc byte[sizeof(ulong)];

        // Little-endian, written explicitly so the result cannot change with the machine's own
        // byte order.
        BinaryPrimitives.WriteUInt64LittleEndian(idBytes, retainerId);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(idBytes, digest);

        // The contract requires 64 lowercase hex characters.
        return Convert.ToHexStringLower(digest);
    }
}
