namespace XIVShinies.SyncPlugin;

/// <summary>
/// The sentences the plugin uses to explain something the website has switched off or does not
/// offer.
/// </summary>
/// <remarks>
/// The collections, the live tracker and the Crucible run sharing draw these on one screen, so one
/// copy keeps a reword from making them disagree. The four differ on purpose: a decision about one
/// thing, that decision for every collection, syncing switched off altogether, and a feature not
/// offered; saying the first during either of the last two sends the user looking for a decision
/// nobody made. Each names the website by its address (see <see cref="HostPlaceholder"/>).
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
    /// What a sharing feature says when the website's <c>/config</c> carries no block for it at all.
    /// </summary>
    /// <remarks>
    /// A missing block means the website does not offer the feature, so nothing was switched off and
    /// nothing is temporary.
    /// </remarks>
    /// <param name="host">The configured website's address.</param>
    public static string NotOffered(string host) => $"Not offered by {host}.";

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
