using Xunit;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Stops a half-loaded character from being cached. A blank name or home world breaks the contract's
// 1-100 character rule, and the plugin treats the server's 400 as its own bug and does not halt, so
// a cached blank identity would fail every upload silently for the rest of the session.
public class CharacterIdentityTests
{
    [Fact]
    public void A_character_with_a_name_and_a_home_world_is_usable()
    {
        Assert.True(CharacterIdentity.IsUsable("Some Name", "Excalibur"));
    }

    // The home world is a lazy game-sheet reference. It reads as empty while the sheet loads, or for a
    // world newer than the installed game data.
    [Theory]
    [InlineData(null, "Excalibur")]
    [InlineData("", "Excalibur")]
    [InlineData("   ", "Excalibur")]
    [InlineData("Some Name", null)]
    [InlineData("Some Name", "")]
    [InlineData("Some Name", "   ")]
    [InlineData(null, null)]
    public void A_character_missing_either_field_is_not_usable(string? name, string? homeWorld)
    {
        Assert.False(CharacterIdentity.IsUsable(name, homeWorld));
    }

    // The server trims the name before it length-checks, so whitespace alone would arrive empty and fail.
    [Fact]
    public void Whitespace_is_not_a_name()
    {
        Assert.False(CharacterIdentity.IsUsable("\t\n ", "Excalibur"));
    }

    // The server accepts at most 100 characters. Real names and worlds are far shorter, so this guards
    // a bad read, which would 400 every upload for the session if cached.
    [Fact]
    public void The_contract_length_bound_is_enforced_on_both_ends()
    {
        var hundred = new string('a', 100);
        var hundredAndOne = new string('a', 101);

        Assert.True(CharacterIdentity.IsUsable(hundred, "Excalibur"));
        Assert.False(CharacterIdentity.IsUsable(hundredAndOne, "Excalibur"));
        Assert.False(CharacterIdentity.IsUsable("Some Name", hundredAndOne));

        // Trimmed before the length check, as the server does.
        Assert.True(CharacterIdentity.IsUsable($"  {hundred}  ", "Excalibur"));
    }
}
