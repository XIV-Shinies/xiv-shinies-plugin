namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// How one collection identifies and describes itself: its wire key, its name, and what it sends.
/// </summary>
/// <remarks>
/// Named properties rather than loose strings, so a call site cannot swap two of them unnoticed. This is
/// the whole of a collection's user-facing identity, and every flag here is self-description: the
/// settings window draws a collection from it without knowing which one it is, so nothing branches on a
/// category name and a new collection appears by existing.
/// </remarks>
public sealed record CategoryInfo
{
    /// <summary>The payload key, which is also the opt-in key and the server's kill-switch key.</summary>
    public required string Key { get; init; }

    /// <summary>The name a person reads, for example <c>"Mounts"</c>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// The heading this collection is listed under on the consent surfaces, for example
    /// <c>"Triple Triad"</c>. Collections sharing a heading are drawn together.
    /// </summary>
    /// <remarks>
    /// The surfaces hold no list of sections, so a new heading arrives with its collection. The heading
    /// also gives a name like "Phantom jobs" its context. It must not contain <c>##</c>, which starts
    /// ImGui's hidden-id syntax in the header label and cuts the visible heading short.
    /// </remarks>
    public required string Section { get; init; }

    /// <summary>
    /// A plain-language sentence naming exactly what leaves the machine for this category. A
    /// compliance surface; see <see cref="ICollector.WhatGetsSent"/>.
    /// </summary>
    public required string WhatGetsSent { get; init; }

    /// <summary>
    /// The detail behind <see cref="WhatGetsSent"/>, shown on hover, or null when the one line says
    /// everything.
    /// </summary>
    /// <remarks>
    /// <see cref="WhatGetsSent"/> must name every kind of data that leaves the machine, including gil and
    /// the per-location scan state sent beside item counts, so a user who reads only that line knows what
    /// they agree to send; a kind of data never moves here. This holds what makes that line trustworthy:
    /// which locations were searched by name, that a count of zero is reported, and that other players
    /// are never involved.
    /// </remarks>
    public string? Details { get; init; }

    /// <summary>
    /// True when the server's item manifest drives this collection's scope, which is otherwise fixed at
    /// compile time. The settings window draws group rows for any collection that sets it.
    /// </summary>
    public bool UsesItemManifest { get; init; }

    /// <summary>
    /// True when a successful read answers for every candidate the server's catalog may hold, so the
    /// collection may declare itself complete on the wire. False, the default, is always safe.
    /// </summary>
    /// <remarks>
    /// The claim turns an id's absence into evidence about the user's own data (see
    /// <see cref="Api.SyncRequest.CollectionScopes"/>), so it must be true;
    /// <c>CompletenessDeclarationTests</c> pins which collections make it. Sweeping a whole game sheet is
    /// not enough: the read must question every catalog candidate or read a source that already is the
    /// character's complete set, and <c>TripleTriadNpcCollector</c> declines for lack of that. A
    /// collection readable only after a player action also needs each pass to earn the claim (see
    /// <c>TamedBeastCollector</c>), and the empty-list floor (see
    /// <see cref="CollectResult.CompleteEnumeration"/>), <c>PayloadCaps</c> and <c>SyncPayloadBuilder</c>
    /// can still withhold it.
    /// </remarks>
    public bool EnumeratesCompleteDomain { get; init; }

    /// <summary>
    /// True when the game announces each new entry the moment it is earned, so the plugin uploads it
    /// within seconds instead of on the scheduled sweep. The sync card reads it to decide whether to
    /// promise that; false only withholds the promise.
    /// </summary>
    /// <remarks>
    /// Registering for the game's unlock signal is not enough: Triple Triad cards are registered like
    /// mounts, but the game never raises the signal for them. Only a collector that routes unlocks
    /// (<see cref="IUnlockAware"/>) passes this to its rows; on any other collection it never reaches
    /// the sync card.
    /// </remarks>
    public bool UploadsOnUnlock { get; init; }
}
