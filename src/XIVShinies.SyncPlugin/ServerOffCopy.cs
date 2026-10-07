namespace XIVShinies.SyncPlugin;

/// <summary>
/// The sentences the plugin uses to explain something the website has switched off or does not
/// offer.
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
/// one names that decision taken for every collection, one names an outage affecting everything,
/// and one names a feature the website does not offer. Saying the first during either of the last
/// two sends the user looking for a decision nobody made.
/// </para>
/// <para>
/// Each names the website by its address, passed in as <c>host</c> (see
/// <see cref="HostPlaceholder"/> for why an address rather than a generic word).
/// </para>
/// </remarks>
// A `static class` holds only shared members and is never instantiated: a module of functions.
// Each member is a method rather than a `const`, because a `const` is fixed when the code compiles
// and the address is only known once the plugin runs. `$"...{host}..."` is an interpolated string,
// like a TypeScript template literal.
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
    /// Each row wears its own "Off" chip, but a whole list of them, all grayed, needs one sentence
    /// above it saying the choice is the website's and the user's own is kept, as during a pause.
    /// </remarks>
    /// <param name="host">The configured website's address.</param>
    public static string EveryCollection(string host) =>
        $"{host} has switched off every collection for now. Your own choices are unchanged.";

    /// <summary>
    /// What everything says while the website has paused syncing altogether.
    /// </summary>
    /// <remarks>
    /// Says plainly that the user's own settings survive, because the visible effect of a pause is
    /// every checkbox clearing at once — which looks exactly like the plugin having discarded their
    /// choices. Nothing is written to their stored consent while this is showing.
    /// </remarks>
    /// <param name="host">The configured website's address.</param>
    public static string Paused(string host) =>
        $"{host} has paused syncing for everyone. Your own choices are unchanged.";
}
