namespace XIVShinies.SyncPlugin.Sync;

/// <summary>How much of a frame's sync work may run.</summary>
public enum TickAction
{
    /// <summary>Nothing at all — the plugin must not contact the server or read the game.</summary>
    Nothing,

    /// <summary>Refresh the server's config, but collect and upload nothing.</summary>
    PollOnly,

    /// <summary>Everything: refresh the config, read the game, upload what is due.</summary>
    Full,
}

/// <summary>
/// How much of the per-frame sync work a tick may do, given consent and the current halt.
/// </summary>
/// <remarks>
/// <para>
/// This is an ordering, and an ordering stated only as a sequence of early returns inside a frame
/// callback is one a test cannot reach — the callback holds Dalamud services and cannot be
/// constructed outside the game. Stating it here keeps the two rules that matter checkable: consent
/// gates absolutely everything, and a halt stops the collecting and uploading without stopping a
/// poll the server would still answer.
/// </para>
/// <para>
/// Nothing here lifts a halt — only the user does, by fixing what caused it. The poll that survives
/// one is how a change on the server's side reaches the plugin meanwhile. Whether it survives is
/// decided by the caller, since it depends on which refusal raised the halt.
/// </para>
/// </remarks>
public static class SyncTickPlan
{
    /// <summary>Decides how much of this frame's work may run.</summary>
    /// <param name="canContactServer">
    /// Whether consent, onboarding and a usable token all permit any request at all. False on a
    /// fresh install, which is what keeps it silent.
    /// </param>
    /// <param name="pollIsWorthwhile">
    /// Whether the halt in force is one <c>/config</c> would answer normally, so a poll is still
    /// worth making. Consulted only while <paramref name="blockedPendingUserAction"/> holds; an
    /// unblocked tick polls regardless.
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
