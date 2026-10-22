using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

// Each character refusal has its own fix. The exact strings are pinned because a sentence naming the
// wrong fix would still look plausible on screen.
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

    // A frame drawn while a logout clears the session has no character name.
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

    [Fact]
    public void An_empty_name_reads_as_this_character()
    {
        Assert.Equal(
            "Claim this character on xiv-shinies.com, then press Sync now.",
            CharacterRefusalCopy.For(ApiStatus.CharacterNotClaimed, "", "Excalibur", Host));
    }

    [Fact]
    public void The_duplicate_claim_sentence_leaves_out_a_missing_world()
    {
        Assert.Equal(
            "You have more than one claim for Some Name on xiv-shinies.com. Remove all but one, " +
            "then press Sync now.",
            CharacterRefusalCopy.For(ApiStatus.CharacterAmbiguous, "Some Name", "", Host));
    }

    // "this character (Excalibur)" would read as if the world were the character's name.
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

    // The sync card treats a status as a character refusal when CharacterRefusalCopy.For returns a
    // sentence. A refusal added to ApiStatusMap without one fails here, instead of drawing as a rejected
    // token in the banner and a self-healing failure in the status line.
    [Fact]
    public void A_sentence_exists_exactly_for_the_character_refusals()
    {
        foreach (var status in Enum.GetValues<ApiStatus>())
        {
            var sentence = CharacterRefusalCopy.For(status, "Some Name", "Excalibur", Host);

            Assert.Equal(ApiStatusMap.IsCharacterRefusal(status), sentence is not null);
        }
    }
}
