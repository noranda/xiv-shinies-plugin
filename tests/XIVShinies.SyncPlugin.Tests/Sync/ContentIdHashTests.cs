using System;
using System.Text.RegularExpressions;
using Xunit;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// The server identifies a character by this digest, permanently. If the byte representation ever
// changes, every already-bound character stops resolving and starts failing with 403. These golden
// vectors were computed independently of the implementation and must never be "updated" to match a
// change — a failure here means the change is wrong, not the test.
public class ContentIdHashTests
{
    // SHA-256 of the ContentId's 8 bytes, little-endian.
    private const string HashOfOne =
        "7c9fa136d4413fa6173637e883b6998d32e1d675f88cddff9dcbcf331820f4b8";

    // Deliberately asymmetric: reversing the byte order changes this digest, so a switch to
    // big-endian cannot pass silently.
    private const string HashOfAsymmetricValue =
        "a85ba2b36261d0dca4b6cbbc840fa8a441ec95200abba5c5623e7ddadeff99e5";

    [Fact]
    public void Locks_the_byte_representation_forever()
    {
        Assert.Equal(HashOfOne, ContentIdHash.Compute(1));
        Assert.Equal(HashOfAsymmetricValue, ContentIdHash.Compute(0x0123456789ABCDEF));
    }

    [Fact]
    public void Produces_exactly_the_shape_the_contract_requires()
    {
        var hash = ContentIdHash.Compute(1234567890123456789);

        // The server validates against ^[0-9a-f]{64}$ and rejects anything else.
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), hash);
    }

    [Fact]
    public void Is_deterministic_across_calls()
    {
        Assert.Equal(ContentIdHash.Compute(42), ContentIdHash.Compute(42));
    }

    [Fact]
    public void Distinguishes_different_characters()
    {
        Assert.NotEqual(ContentIdHash.Compute(42), ContentIdHash.Compute(43));
    }

    // Zero is the game's "no character loaded" sentinel. Hashing it would bind a real account to a
    // digest that means nothing, so refuse rather than produce a plausible-looking hash.
    [Fact]
    public void Refuses_to_hash_the_no_character_sentinel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContentIdHash.Compute(0));
    }
}
