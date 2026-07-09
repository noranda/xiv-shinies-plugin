using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Turns the local character's ContentId into the opaque identifier the server knows it by.
/// </summary>
/// <remarks>
/// <para>
/// The raw ContentId <b>never leaves this process</b>. Dalamud asks plugins to hash player
/// identifiers client-side, and this project treats that as a hard requirement: only the digest is
/// sent, logged, or persisted.
/// </para>
/// <para>
/// <b>The byte representation below is permanent.</b> The server binds a character to this digest
/// on first upload and resolves it by digest thereafter — that is what lets a character survive a
/// rename or a world transfer. Change the byte order, the hash algorithm, or the casing, and every
/// already-bound character stops resolving and begins failing with <c>403 character_not_claimed</c>.
/// Golden vectors in the tests pin all three; a failure there means the change is wrong.
/// </para>
/// </remarks>
public static class ContentIdHash
{
    /// <summary>Computes the lowercase-hex SHA-256 digest of a ContentId.</summary>
    /// <param name="contentId">The local character's ContentId. Must not be zero.</param>
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

        // `stackalloc` puts these bytes on the stack rather than the heap — no allocation, and they
        // vanish when the method returns. There is no JS equivalent.
        Span<byte> idBytes = stackalloc byte[sizeof(ulong)];

        // Little-endian, chosen once and frozen. Writing it explicitly rather than using
        // BitConverter means the result cannot change with the machine's own endianness.
        BinaryPrimitives.WriteUInt64LittleEndian(idBytes, contentId);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(idBytes, digest);

        // The contract requires lowercase hex: it validates against ^[0-9a-f]{64}$.
        return Convert.ToHexStringLower(digest);
    }
}
