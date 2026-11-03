using System;
using System.Text.RegularExpressions;
using Xunit;
using XIVShinies.SyncPlugin.Glamour;

namespace XIVShinies.SyncPlugin.Tests.Glamour;

// The digest is permanent (RetainerIdHash's remarks say why). The vectors were computed
// independently: on a failure, fix the code.
public class RetainerIdHashTests
{
    // SHA-256 of the retainer id's 8 bytes, little-endian, in lowercase hex.
    private const string HashOfOne =
        "7c9fa136d4413fa6173637e883b6998d32e1d675f88cddff9dcbcf331820f4b8";

    // Its bytes are asymmetric, so a switch to big-endian changes this digest.
    private const string HashOfAsymmetricValue =
        "a85ba2b36261d0dca4b6cbbc840fa8a441ec95200abba5c5623e7ddadeff99e5";

    [Fact]
    public void Locks_the_byte_representation_forever()
    {
        Assert.Equal(HashOfOne, RetainerIdHash.Compute(1));
        Assert.Equal(HashOfAsymmetricValue, RetainerIdHash.Compute(0x0123456789ABCDEF));
    }

    [Fact]
    public void Produces_exactly_the_shape_the_contract_requires()
    {
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), RetainerIdHash.Compute(33_000_000_123));
    }

    [Fact]
    public void Distinguishes_different_retainers()
    {
        Assert.NotEqual(RetainerIdHash.Compute(42), RetainerIdHash.Compute(43));
    }

    // The game stores zero in an unused entry (RetainerIdHash.Compute says why it is refused).
    [Fact]
    public void Refuses_to_hash_an_unused_entry()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetainerIdHash.Compute(0));
    }
}
