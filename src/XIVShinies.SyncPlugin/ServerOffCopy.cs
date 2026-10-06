namespace XIVShinies.SyncPlugin;

/// <summary>
/// The sentences the plugin uses to explain something the server has switched off.
/// </summary>
/// <remarks>
/// <para>
/// Every surface that reports a server-side "off" reaches for one of these, and they sit here,
/// owned by no single feature, because the collections, the live tracker and the Crucible run
/// sharing all use them. Their rows are drawn on the same screen, so two copies of these strings
/// would let a reword leave them saying different things about the same state, in view of each
/// other.
/// </para>
/// <para>
/// The distinctions between them are the whole point: one names a decision about a single thing,
/// one names that decision taken for every collection, and one names an outage affecting
/// everything. Saying the first during the last sends the user looking for a decision nobody made.
/// </para>
/// <para>
/// All three name the server generically. The backend URL is a user-overridable setting, so the thing
/// that switched a collection off is whichever server this install points at. Sentences that say
/// where data goes or where the user must act name the configured host itself, which these cannot:
/// they are constants, with no host to interpolate.
/// </para>
/// </remarks>
public static class ServerOffCopy
{
    /// <summary>
    /// What one switched-off collection or feature says when the server offered no note of its own.
    /// </summary>
    public const string Feature = "Temporarily switched off by the server.";

    /// <summary>
    /// What a consent surface or the sync card says while the server has switched off every
    /// collection one by one, without pausing syncing.
    /// </summary>
    /// <remarks>
    /// Each row wears its own "Off" chip, but a whole list of them, all grayed, needs one sentence
    /// above it saying the choice is the server's and the user's own is kept, as during a pause.
    /// </remarks>
    public const string EveryCollection =
        "The server has switched off every collection for now. Your own choices are unchanged.";

    /// <summary>
    /// What everything says while the server has paused syncing altogether.
    /// </summary>
    /// <remarks>
    /// Says plainly that the user's own settings survive, because the visible effect of a pause is
    /// every checkbox clearing at once — which looks exactly like the plugin having discarded their
    /// choices. Nothing is written to their stored consent while this is showing.
    /// </remarks>
    public const string Paused =
        "The server has paused syncing for everyone. Your own choices are unchanged.";
}
