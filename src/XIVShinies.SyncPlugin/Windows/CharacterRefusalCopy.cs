using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// What the sync card tells a player whose upload the server refused because it could not match
/// their character: one sentence per refusal, each naming the fix that refusal needs.
/// </summary>
/// <remarks>
/// <para>
/// The four refusals look alike from inside the game (the upload was refused) but are fixed in
/// different places: a claim to make on the website, a Lodestone bio code to add, a duplicate claim
/// to remove, or a link only the site's admins can change. A sentence that named the wrong fix would
/// send the player to a page with nothing on it for them, so the wording lives here, where the
/// tests pin every sentence exactly.
/// </para>
/// <para>
/// The name and home world are the local character's own, read from the game, not the copies the
/// server echoes in the refusal's body. They are the same values on an honest server, and the local
/// ones need no bounding before they are drawn.
/// </para>
/// </remarks>
public static class CharacterRefusalCopy
{
    /// <summary>
    /// The sentence for a character refusal, or null when <paramref name="status"/> is not one.
    /// </summary>
    /// <param name="status">The refused upload's status.</param>
    /// <param name="characterName">The local character's name, or null when none is known yet.</param>
    /// <param name="homeWorld">The local character's home world, or null or empty when unknown.</param>
    /// <param name="host">The configured server's host, which every sentence names.</param>
    public static string? For(ApiStatus status, string? characterName, string? homeWorld, string host)
    {
        // "this character" mid-sentence, "This character" when it opens one. The `is { Length: > 0 }`
        // pattern reads "is a string with at least one character", so an empty name counts as none.
        var name = characterName is { Length: > 0 } ? characterName : "this character";
        var subject = characterName is { Length: > 0 } ? characterName : "This character";

        return status switch
        {
            ApiStatus.CharacterNotClaimed =>
                $"Claim {name} on {host}, then press Sync now.",

            ApiStatus.CharacterNotVerified =>
                $"Verify {name} on {host} by adding the code to your Lodestone bio, then press Sync now.",

            // The one sentence that names the home world: the duplicate claims share both name and
            // world, and naming both tells the player exactly which of their claims to look
            // through. "Name (World)" matches how the account panel lists claimed characters.
            ApiStatus.CharacterAmbiguous =>
                $"You have more than one claim for {WithWorld(name, characterName, homeWorld)} on {host}. " +
                "Remove all but one, then press Sync now.",

            // Relinking is done by the site's admins, so the player asks in Discord.
            ApiStatus.CharacterBoundElsewhere =>
                $"{subject} is linked to a different game character on {host}. " +
                "Ask for help in Discord to relink it, then press Sync now.",

            _ => null,
        };
    }

    /// <summary>
    /// The name followed by its home world in parentheses, when both are known; the name alone
    /// otherwise.
    /// </summary>
    /// <param name="name">The name as the sentence will print it, "this character" included.</param>
    /// <param name="characterName">The real name, or null, which decides whether a world applies.</param>
    /// <param name="homeWorld">The home world, or null or empty.</param>
    private static string WithWorld(string name, string? characterName, string? homeWorld) =>
        characterName is { Length: > 0 } && homeWorld is { Length: > 0 }
            ? $"{name} ({homeWorld})"
            : name;
}
