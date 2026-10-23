using System;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>How much of a frame's sync work may run.</summary>
public enum TickAction
{
    /// <summary>Nothing at all: the plugin must not contact the server or read the game.</summary>
    Nothing,

    /// <summary>Refresh the server's config, but collect and upload nothing.</summary>
    PollOnly,

    /// <summary>Everything: refresh the config, read the game, upload what is due.</summary>
    Full,
}

/// <summary>
/// How much of the per-frame sync work a tick may do, given consent and the current halt, and when the
/// next <c>/config</c> poll falls once it is brought forward.
/// </summary>
/// <remarks>
/// Consent gates everything, and a halt stops collecting and uploading without stopping a poll the
/// server would still answer. Only the user lifts a halt; the poll that survives it is how a change
/// on the server's side reaches the plugin meanwhile.
/// </remarks>
public static class SyncTickPlan
{
    /// <summary>
    /// Whether a config poll is still worth making under the halt raised by
    /// <paramref name="haltStatus"/>.
    /// </summary>
    /// <param name="haltStatus">The status that raised the halt, or null when there is none.</param>
    /// <remarks>
    /// <c>/config</c> answers 200 or 401, never 403, so a character refusal (any of the four 403s)
    /// leaves the poll working, and the poll shows when a server pause lifts while the player sorts out
    /// the claim. A token-shaped halt would draw the same 401 every poll, and a backend the client
    /// refused to use sends nothing, so neither is worth polling through.
    /// </remarks>
    public static bool PollSurvivesHalt(ApiStatus? haltStatus) =>
        haltStatus is { } status && ApiStatusMap.IsCharacterRefusal(status);

    /// <summary>
    /// When the next <c>/config</c> poll is due once it is brought forward to <paramref name="soon"/>.
    /// </summary>
    /// <param name="due">When it was due, or null for "at the first opportunity".</param>
    /// <param name="soon">The latest it should now wait.</param>
    /// <remarks>
    /// Never pushes a poll back: one already due sooner keeps its time, and one due at the first
    /// opportunity stays so.
    /// </remarks>
    public static DateTimeOffset? BringPollForward(DateTimeOffset? due, DateTimeOffset soon) =>
        due is { } d && d > soon ? soon : due;

    /// <summary>Decides how much of this frame's work may run.</summary>
    /// <param name="canContactServer">
    /// Whether consent, onboarding and a usable token all permit any request at all. False on a
    /// fresh install, which is what keeps it silent.
    /// </param>
    /// <param name="pollIsWorthwhile">
    /// Whether <c>/config</c> would answer the halt in force normally. Consulted only while
    /// <paramref name="blockedPendingUserAction"/> holds.
    /// </param>
    /// <param name="blockedPendingUserAction">
    /// Whether syncing has stopped for something only the user can resolve.
    /// </param>
    public static TickAction Decide(
        bool canContactServer,
        bool pollIsWorthwhile,
        bool blockedPendingUserAction)
    {
        if (!canContactServer)
            return TickAction.Nothing;

        if (blockedPendingUserAction)
            return pollIsWorthwhile ? TickAction.PollOnly : TickAction.Nothing;

        return TickAction.Full;
    }
}
