using System;
using System.Text.RegularExpressions;
using Xunit;
using XIVShinies.SyncPlugin.Glamour;

namespace XIVShinies.SyncPlugin.Tests.Glamour;

// The server stores a retainer under this digest and keeps its last-known name against it, so the
// digest must never change for the same retainer. These golden vectors were computed independently
// of the implementation (SHA-256 of the id's 8 bytes, little-endian, lowercase hex) and must never be
// "updated" to match a change: a failure here means the change is wrong, not the test.
public class RetainerIdHashTests
{
    private const string HashOfOne =
        "7c9fa136d4413fa6173637e883b6998d32e1d675f88cddff9dcbcf331820f4b8";

    // Deliberately asymmetric: reversing the byte order changes this digest, so a switch to
    // big-endian cannot pass silently.
    private const string HashOfAsymmetricValue =
        "a85ba2b36261d0dca4b6cbbc840fa8a441ec95200abba5c5623e7ddadeff99e5";

    [Fact]
    public void Locks_the_byte_representation_forever()
    {
        Assert.Equal(HashOfOne, RetainerIdHash.Compute(1));
        Assert.Equal(HashOfAsymmetricValue, RetainerIdHash.Compute(0x0123456789ABCDEF));
    }

    // The contract validates the digest as 64 lowercase hex characters.
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

    // Zero is an unused entry (RetainerIdHash.Compute's exception doc says why it is refused).
    [Fact]
    public void Refuses_to_hash_an_unused_entry()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetainerIdHash.Compute(0));
    }
}
