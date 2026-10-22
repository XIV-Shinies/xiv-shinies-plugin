using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// One sentence for each way the server refuses an upload because it cannot match the character,
/// each naming that refusal's fix.
/// </summary>
/// <remarks>
/// The four look alike in game but are fixed in different places: a claim to make on the website, a
/// Lodestone bio code to add, a duplicate claim to remove, or a link only the site's admins can
/// change. The name and home world come from the game, not the refusal's body, so they need no
/// bounding.
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
        // "this character" mid-sentence, "This character" when it opens one. An empty name counts as
        // none.
        var name = characterName is { Length: > 0 } ? characterName : "this character";
        var subject = characterName is { Length: > 0 } ? characterName : "This character";

        return status switch
        {
            ApiStatus.CharacterNotClaimed =>
                $"Claim {name} on {host}, then press Sync now.",

            ApiStatus.CharacterNotVerified =>
                $"Verify {name} on {host} by adding the code to your Lodestone bio, then press Sync now.",

            // Only this sentence names the world, so the player knows which duplicate claims to look
            // through. "Name (World)" matches the account panel's list.
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
