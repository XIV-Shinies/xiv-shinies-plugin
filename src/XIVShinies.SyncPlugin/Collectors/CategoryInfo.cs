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
    /// True when this collection is read and sent only once the server's <c>/config</c> names it in its
    /// category map and switches it on.
    /// </summary>
    /// <remarks>
    /// False, the default, reads a key the map never names as on: the server drops payload keys it does
    /// not know, so an unknown collection costs a few bytes. Set it for a collection worth sending only
    /// to a server that stores it, one whose snapshot is large or whose record would be a disclosure for
    /// nothing; <c>SelfDescriptionDeclarationTests</c> pins which collections set it. Until the server
    /// names it, <see cref="CollectorGate.ServerPermits"/> keeps it unread and unsent, and the settings
    /// window draws it as unavailable (see <see cref="CategorySettingsRow.NotOfferedByServer"/>).
    /// </remarks>
    public bool RequiresServerSupport { get; init; }

    /// <summary>
    /// True when the facts are one record about the character rather than a collection of things, so
    /// the upload log names the category without a count.
    /// </summary>
    /// <remarks>
    /// A record has no "how many": counted from its shape it would show its number of fields, which moves
    /// with the payload's layout rather than anything the player did, and even a count of one reads as
    /// "one of something". The record's fingerprint still drives the log's "(changed)" mark.
    /// <c>SelfDescriptionDeclarationTests</c> pins which collections set it.
    /// </remarks>
    public bool IsSingleRecord { get; init; }

    /// <summary>
    /// True when the facts come from the character's storage containers (inventory, saddlebag,
    /// retainers, Armoire, Glamour Dresser) and each pass reports their scan state.
    /// </summary>
    /// <remarks>
    /// While such a collection is on, the "Reading from:" panel lists each container, whether it could
    /// be read and, when not, which window to open (see <see cref="ReadStatusView.Build"/>). Separate
    /// from <see cref="UsesItemManifest"/>: a collection can read storage without the item manifest
    /// scoping it. <c>SelfDescriptionDeclarationTests</c> pins which collections set it.
    /// </remarks>
    public bool ReadsStorage { get; init; }

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

    /// <summary>
    /// The sync response key the server sends only once it has stored this collection's facts, or null
    /// when the facts go with every upload. When set, each fact goes until a response confirms it.
    /// </summary>
    /// <remarks>
    /// A key the server sends for any other reason would drop facts it never stored;
    /// <c>StoreConfirmationDeclarationTests</c> pins each one.
    /// </remarks>
    public string? StoredResponseKey { get; init; }
}
