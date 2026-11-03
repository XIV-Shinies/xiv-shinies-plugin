namespace XIVShinies.SyncPlugin;

/// <summary>
/// The sentences the plugin uses to explain something the website has switched off, does not
/// offer, or has not answered about.
/// </summary>
/// <remarks>
/// The collections, the live tracker and the Crucible run sharing draw these on one screen, so one
/// copy keeps a reword from making them disagree. They differ on purpose: a decision about one thing,
/// that decision for every collection, syncing switched off altogether, something not offered (a
/// sharing feature with no <c>/config</c> block, or a collection that needs naming and is not named),
/// and no answer before any <c>/config</c>; saying the first during any of the last three sends
/// the user looking for a decision nobody made. Each names the website by its address (see
/// <see cref="HostPlaceholder"/>).
/// </remarks>
// Methods rather than consts, because the address is known only once the plugin runs.
public static class ServerOffCopy
{
    /// <summary>
    /// What one switched-off collection or feature says when the website offered no note of its own.
    /// </summary>
    /// <param name="host">The configured website's address.</param>
    public static string Feature(string host) => $"Temporarily switched off by {host}.";

    /// <summary>
    /// What a sharing feature says when the website's <c>/config</c> carries no block for it at all,
    /// and what a collection that needs the website to name it says when the website has not.
    /// </summary>
    /// <remarks>
    /// Either way the website has made no decision to switch anything off, so nothing is temporary,
    /// and the sentence does not borrow <see cref="Feature"/>'s wording, which would promise a
    /// decision and a return that nobody made.
    /// </remarks>
    /// <param name="host">The configured website's address.</param>
    public static string NotOffered(string host) => $"Not offered by {host}.";

    /// <summary>
    /// What a collection that needs the website to name it says before any <c>/config</c> has
    /// arrived to say whether the website offers it.
    /// </summary>
    /// <remarks>
    /// Nothing is known, so the sentence claims neither that the website offers the collection nor
    /// that it does not; <see cref="NotOffered"/> takes over once the answer leaves it unnamed.
    /// </remarks>
    /// <param name="host">The configured website's address.</param>
    public static string AwaitingAnswer(string host) =>
        $"Waiting for {host} to say whether it offers this.";

    /// <summary>
    /// What a consent surface or the sync card says while the website has switched off every
    /// collection one by one, without pausing syncing.
    /// </summary>
    /// <remarks>
    /// Each row wears its own "Off" chip, but a whole grayed list needs one sentence above it saying
    /// the choice is the website's and the user's own is kept.
    /// </remarks>
    /// <param name="host">The configured website's address.</param>
    public static string EveryCollection(string host) =>
        $"{host} has switched off every collection for now. Your own choices are unchanged.";

    /// <summary>
    /// What everything says while syncing is off altogether on the website.
    /// </summary>
    /// <remarks>
    /// The website sends the same <c>enabled: false</c> for its own pause and for the "Sync from the
    /// plugin" switch on the player's profile, and the plugin cannot tell them apart, so the sentence
    /// names no audience and points at the switch the player can flip. A pause clears every checkbox at
    /// once, which looks like the plugin discarded the user's choices, so this says plainly that they
    /// survive. Nothing is written to their stored consent while it shows.
    /// </remarks>
    /// <param name="host">The configured website's address.</param>
    public static string Paused(string host) =>
        $"Syncing is off on {host}. If you turned off \"Sync from the plugin\" on your profile, "
        + "turn it back on there. Your own choices are unchanged.";
}
