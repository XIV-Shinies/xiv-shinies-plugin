using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

// What the plugin tells a player whose upload the server refused because it could not match
// their character. Each refusal has a different fix, so each sentence names that fix,
// and the exact strings are pinned: a sentence that named the wrong fix would still look plausible
// on screen.
public class CharacterRefusalCopyTests
{
    private const string Host = "xiv-shinies.com";

    [Theory]
    [InlineData(ApiStatus.CharacterNotClaimed,
        "Claim Some Name on xiv-shinies.com, then press Sync now.")]
    [InlineData(ApiStatus.CharacterNotVerified,
        "Verify Some Name on xiv-shinies.com by adding the code to your Lodestone bio, then press " +
        "Sync now.")]
    [InlineData(ApiStatus.CharacterAmbiguous,
        "You have more than one claim for Some Name (Excalibur) on xiv-shinies.com. Remove all " +
        "but one, then press Sync now.")]
    [InlineData(ApiStatus.CharacterBoundElsewhere,
        "Some Name is linked to a different game character on xiv-shinies.com. Ask for help in " +
        "Discord to relink it, then press Sync now.")]
    public void Each_refusal_names_its_own_fix(ApiStatus status, string expected)
    {
        Assert.Equal(expected, CharacterRefusalCopy.For(status, "Some Name", "Excalibur", Host));
    }

    // The character can be unknown to the window (a frame drawn while a logout is clearing the
    // session), so every sentence has a form that reads naturally without a name.
    [Theory]
    [InlineData(ApiStatus.CharacterNotClaimed,
        "Claim this character on xiv-shinies.com, then press Sync now.")]
    [InlineData(ApiStatus.CharacterNotVerified,
        "Verify this character on xiv-shinies.com by adding the code to your Lodestone bio, then " +
        "press Sync now.")]
    [InlineData(ApiStatus.CharacterAmbiguous,
        "You have more than one claim for this character on xiv-shinies.com. Remove all but one, " +
        "then press Sync now.")]
    [InlineData(ApiStatus.CharacterBoundElsewhere,
        "This character is linked to a different game character on xiv-shinies.com. Ask for help " +
        "in Discord to relink it, then press Sync now.")]
    public void Each_refusal_reads_naturally_without_a_name(ApiStatus status, string expected)
    {
        Assert.Equal(expected, CharacterRefusalCopy.For(status, null, null, Host));
    }

    // An empty name is no name: the sentence says "this character", never a blank.
    [Fact]
    public void An_empty_name_reads_as_this_character()
    {
        Assert.Equal(
            "Claim this character on xiv-shinies.com, then press Sync now.",
            CharacterRefusalCopy.For(ApiStatus.CharacterNotClaimed, "", "Excalibur", Host));
    }

    // Without a world, the duplicate-claim sentence names the character alone.
    [Fact]
    public void The_duplicate_claim_sentence_leaves_out_a_missing_world()
    {
        Assert.Equal(
            "You have more than one claim for Some Name on xiv-shinies.com. Remove all but one, " +
            "then press Sync now.",
            CharacterRefusalCopy.For(ApiStatus.CharacterAmbiguous, "Some Name", "", Host));
    }

    // A world with no name to attach it to is left out too: "this character (Excalibur)" would
    // read as if the world were the character's name.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void The_duplicate_claim_sentence_leaves_out_a_world_without_a_name(string? characterName)
    {
        Assert.Equal(
            "You have more than one claim for this character on xiv-shinies.com. Remove all but one, " +
            "then press Sync now.",
            CharacterRefusalCopy.For(ApiStatus.CharacterAmbiguous, characterName, "Excalibur", Host));
    }

    // Every status, not a hand-picked few: a sentence exists exactly for the statuses the status
    // map calls character refusals. The sync card relies on this to find the refusals, so a new
    // refusal added to the map without a sentence fails here rather than being drawn as a rejected
    // token in the banner and as a self-healing failure in the status line.
    [Fact]
    public void A_sentence_exists_exactly_for_the_character_refusals()
    {
        // Enum.GetValues<T>() returns every member of the enum as an array, as Object.values does
        // for a TS string enum.
        foreach (var status in Enum.GetValues<ApiStatus>())
        {
            var sentence = CharacterRefusalCopy.For(status, "Some Name", "Excalibur", Host);

            Assert.Equal(ApiStatusMap.IsCharacterRefusal(status), sentence is not null);
        }
    }
}
