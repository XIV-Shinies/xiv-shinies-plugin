using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// The sentence that tells a player how to end a halt: syncing stopped until they fix something.
/// Drawn on the sync card and, while the halt is the only thing stopping either one, on the live
/// tracker's card and the Crucible run sharing's card.
/// </summary>
/// <remarks>
/// A halt has three kinds of cause, each with its own fix: a character refusal (the four
/// sentences in <see cref="CharacterRefusalCopy"/>), an unusable server setting on this machine
/// (worded by <see cref="BackendUrl.DescribeUnusableSetting"/>), or a rejected token, the only
/// other status that raises a halt. One class decides among them, so every card that shows a halt
/// says the same thing about it.
/// </remarks>
public static class HaltCopy
{
    /// <summary>The sentence for the halt raised by <paramref name="halt"/>.</summary>
    /// <param name="halt">
    /// The status that raised the halt, or null when it is not known (the frame a halt is being
    /// cleared), which reads as a token halt rather than as nothing.
    /// </param>
    /// <param name="characterName">The local character's name, or null when none is loaded.</param>
    /// <param name="homeWorld">The local character's home world, or null or empty.</param>
    /// <param name="host">The configured server's host, which every website fix names.</param>
    /// <param name="baseUrl">The configured server address, for the settings sentence.</param>
    /// <param name="customBackendAcknowledged">
    /// Whether a server this project does not run has been acknowledged, for the settings sentence.
    /// </param>
    public static string For(
        ApiStatus? halt,
        string? characterName,
        string? homeWorld,
        string host,
        string? baseUrl,
        bool customBackendAcknowledged) =>
        halt switch
        {
            ApiStatus.NotConfigured => BackendUrl.DescribeUnusableSetting(baseUrl, customBackendAcknowledged),

            // `{ } refused` matches any non-null status and names it `refused`. The `when` guard
            // keeps this arm only if the refusal copy has a sentence for that status, and
            // `is { } sentence` names the sentence so the arm can return it.
            { } refused
                when CharacterRefusalCopy.For(refused, characterName, homeWorld, host) is { } sentence =>
                sentence,

            _ => $"Your token was rejected. Generate a new one on {host}, paste it under Account, " +
                 "then press Sync now.",
        };
}
