namespace XIVShinies.SyncPlugin;

/// <summary>
/// The sentences the plugin uses to explain something the server is not accepting: switched off,
/// paused for everyone, or not offered yet.
/// </summary>
/// <remarks>
/// <para>
/// Every surface that reports a server-side "off" reaches for one of these, and they sit here —
/// owned by neither the collections nor the occult tracker — because <see cref="Feature"/> and
/// <see cref="Paused"/> belong to both. A collection row and the live tracker's row are drawn on the
/// same screen, so two copies of these strings would let a reword leave them saying different
/// things about the same state, in view of each other. <see cref="NotOffered"/> is drawn by
/// collection rows only, and sits beside the other two so the three are worded as a set.
/// </para>
/// <para>
/// The distinctions between them are the whole point. <see cref="Feature"/> names a decision about
/// a single thing, <see cref="Paused"/> names an outage affecting everything, and
/// <see cref="NotOffered"/> names the absence of any decision at all. Saying one in place of
/// another sends the user looking for a decision nobody made.
/// </para>
/// <para>
/// All of them name the server generically. The backend URL is a user-overridable setting, so the
/// server these sentences speak of is whichever one this install points at. Sentences that say
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

    /// <summary>
    /// What a collection says when it needs the server to name it and the server has not.
    /// </summary>
    /// <remarks>
    /// A collection the server has never named is not a decision to switch it off — the server may
    /// not know the collection at all — so it does not borrow <see cref="Feature"/>'s "temporarily
    /// switched off" wording, which would promise a decision and a return that nobody made.
    /// </remarks>
    public const string NotOffered = "Not offered by the server yet.";
}
