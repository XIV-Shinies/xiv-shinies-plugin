namespace XIVShinies.SyncPlugin;

/// <summary>
/// The two sentences the plugin uses to explain something the server has switched off.
/// </summary>
/// <remarks>
/// <para>
/// Every surface that reports a server-side "off" reaches for one of these, and they sit here —
/// owned by neither the collections nor the occult tracker — because they belong to both. A
/// collection row and the live tracker's row are drawn on the same screen, so two copies of these
/// strings would let a reword leave them saying different things about the same state, in view of
/// each other.
/// </para>
/// <para>
/// The distinction between them is the whole point: one names a decision about a single thing, the
/// other names an outage affecting everything. Saying the first during the second sends the user
/// looking for a decision nobody made.
/// </para>
/// <para>
/// Both name the server generically. The backend URL is a user-overridable setting, so the thing
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
