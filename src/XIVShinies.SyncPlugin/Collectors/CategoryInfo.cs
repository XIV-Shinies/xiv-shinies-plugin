namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// How one collection identifies and describes itself: its wire key, its name, and what it sends.
/// </summary>
/// <remarks>
/// <para>
/// Grouped into a single value rather than passed as three loose strings, because three adjacent
/// <c>string</c> parameters are trivially easy to hand over in the wrong order — and the compiler
/// would never notice. Here the call site names each one.
/// </para>
/// <para>
/// This is the whole of a collection's user-facing identity. The settings window renders it without
/// knowing which collection it is looking at, which is what lets a new collection appear in the UI
/// by existing rather than by being added to a list somewhere.
/// </para>
/// </remarks>
public sealed record CategoryInfo
{
    /// <summary>The payload key, the opt-in key, and the server's kill-switch key, all at once.</summary>
    public required string Key { get; init; }

    /// <summary>The name a person reads, for example <c>"Mounts"</c>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// The heading this collection is listed under on the consent surfaces, for example
    /// <c>"Triple Triad"</c>. Collections sharing a section title are drawn together.
    /// </summary>
    /// <remarks>
    /// Self-description like <see cref="DisplayName"/>: the consent surfaces group rows by
    /// whatever section titles the collectors declare, holding no list of their own — a
    /// collection declaring a brand-new section brings its heading with it, and the UI draws it
    /// without being taught. This is also what gives a name like "Phantom jobs" its context: the
    /// section heading says which part of the game it belongs to. The title must not contain
    /// <c>##</c> — it becomes part of an ImGui header label, where <c>##</c> begins the
    /// hidden-id syntax and would cut the visible heading short.
    /// </remarks>
    public required string Section { get; init; }

    /// <summary>
    /// A plain-language sentence naming exactly what leaves the machine for this category.
    /// </summary>
    /// <remarks>
    /// A compliance surface: Dalamud requires the user be told what is collected before consenting.
    /// It must describe the real payload, and must be revised whenever the collector starts sending
    /// something new.
    /// </remarks>
    public required string WhatGetsSent { get; init; }

    /// <summary>
    /// The elaboration behind <see cref="WhatGetsSent"/> — where the plugin looks, what edge cases
    /// count, what is <i>not</i> involved — or null when the one-liner says everything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shown on hover, so the consent list stays scannable while the full story remains one gesture
    /// away. The split is load-bearing for compliance, and the dividing line is strict:
    /// <see cref="WhatGetsSent"/> must name <b>every kind of data that leaves the machine</b>, so a
    /// user who reads only the visible line still knows what they are agreeing to send. A kind of
    /// data may never be demoted here — gil belongs on the visible line, and so does the fact that
    /// a per-location scan state travels beside the item counts.
    /// </para>
    /// <para>
    /// What belongs here instead: which locations were searched, by name; that a count of zero is
    /// itself reported; that other players are never involved. Detail that makes the visible line
    /// <i>trustworthy</i> rather than detail that changes what it discloses.
    /// </para>
    /// </remarks>
    public string? Details { get; init; }

    /// <summary>
    /// True when this collection's scope is driven by the server's item manifest, rather than being
    /// fixed at compile time (as quests, mounts, minions, and achievements are).
    /// </summary>
    /// <remarks>
    /// This is <b>self-description, not a category-name branch</b>: the settings window asks a
    /// collector "do you want group rows?" through this flag instead of asking "are you the items
    /// collector?" by comparing keys. A future manifest-driven collection sets this to true on its own
    /// <see cref="CategoryInfo"/> and gets the same group-row treatment automatically — nothing
    /// downstream needs to learn its name. Defaults to <c>false</c>, matching every existing
    /// fixed-scope collection.
    /// </remarks>
    public bool UsesItemManifest { get; init; }

    /// <summary>
    /// True when a successful read of this collection produces an answer for <b>every</b> candidate
    /// the server's catalog may hold, so it can declare itself complete on the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The highest-stakes claim the plugin makes: it is what turns an id's <i>absence</i> into
    /// evidence (see <see cref="Api.SyncRequest.CollectionScopes"/> for what that licenses). The
    /// assertion is about the user's own data, so it must be true.
    /// </para>
    /// <para>
    /// Walking a whole game sheet is one way to get there, and is necessary but <b>not
    /// sufficient</b>. The test is whether the read answers for every candidate the <b>server's
    /// catalog</b> may hold — by questioning each one, or by reading a source that already IS the
    /// character's complete set. A collection that can only reach part of that set must leave this
    /// false even though its own sweep was exhaustive; <c>TripleTriadNpcCollector</c>'s class
    /// remarks are the worked example of declining for that reason.
    /// </para>
    /// <para>
    /// Self-description in the same sense as <see cref="UsesItemManifest"/>: a collector reads this
    /// flag rather than deciding entitlement for itself, so the decision sits beside the category
    /// it describes and one test can pin the whole set. A collection whose domain only becomes
    /// readable on the player's action requires its own pass to have earned the claim as well —
    /// <c>TamedBeastCollector</c> is the worked example. Declaring it is a request, not a guarantee:
    /// <see cref="CollectResult.CompleteEnumeration"/> describes the floor applied at the factory,
    /// and further gates on <c>PayloadCaps</c> and <c>SyncPayloadBuilder</c> can withhold it later
    /// still. Defaults to <c>false</c>, which is always safe: it withholds a claim rather than
    /// making a wrong one.
    /// </para>
    /// </remarks>
    public bool EnumeratesCompleteDomain { get; init; }

    /// <summary>
    /// True when this collection may be collected only once the server's <c>/config</c> names it in
    /// its category map and switches it on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Self-description in the same sense as <see cref="UsesItemManifest"/>: the gate asks the
    /// collector "do you need the server to know you?" instead of keeping a list of collections
    /// that do, so the stricter rule reaches a collection by its own declaration and nothing
    /// downstream learns its name.
    /// </para>
    /// <para>
    /// <c>false</c> (the default) keeps the ordinary rule that a category the server's map does not
    /// name reads as <b>enabled</b>: the server strips payload keys it does not recognize, so a
    /// collection the server does not know costs only a few discarded bytes. <c>true</c> turns that
    /// around, so an absent key reads as <b>off</b>. It is for a collection only worth uploading to a
    /// server that knows it — one whose snapshot is large enough that sending it to a server that
    /// will discard it is a real cost, or one whose record would be a disclosure for nothing on a
    /// server that does not store it. Until the server names it, such a collection is neither read
    /// nor sent (see <see cref="CollectorGate.ServerPermits"/>), and the settings window draws it as
    /// not offered (see <see cref="CategorySettingsRow.NotOfferedByServer"/>).
    /// </para>
    /// </remarks>
    public bool RequiresServerSupport { get; init; }

    /// <summary>
    /// True when this collection's facts are <b>one record</b> about the character rather than a
    /// collection of things, so the upload log names it without a count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The upload log prints a count beside each category it sent, and for a collection of things
    /// that count is the answer to "how many?". A single record has no such answer: counted from its
    /// shape it would read as the number of fields in the record, a figure that moves with the
    /// payload's layout rather than with anything the player did, and even a fixed count of one
    /// would read as "one of something". So the log prints the name alone, and the record's
    /// fingerprint carries its "(changed)" signal.
    /// </para>
    /// <para>
    /// Self-description like <see cref="UsesItemManifest"/>: the log reads this flag rather than a
    /// list of single-record categories. Defaults to <c>false</c>, matching every collection of
    /// things.
    /// </para>
    /// </remarks>
    public bool IsSingleRecord { get; init; }

    /// <summary>
    /// True when this collection's facts come from the character's storage containers (inventory,
    /// saddlebag, retainers, Armoire, Glamour Dresser) and its pass reports those containers' scan
    /// state, so the settings panel shows the container lines while it is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The settings window's "Reading from:" panel has a container group — one line per storage
    /// location, saying whether it could be read and, when it could not, which window to open. It
    /// shows only while a collection that reads storage is switched on (see
    /// <see cref="ReadStatusView.Build"/>).
    /// </para>
    /// <para>
    /// Separate from <see cref="UsesItemManifest"/> because the two answer different questions. A
    /// collection can read storage without its scope coming from the server's item manifest, and it
    /// depends on the container guidance just as much. Self-description like the flags above: the
    /// panel asks the row "do you read storage?" instead of comparing keys, so a new storage-reading
    /// collection brings the container lines with it by setting this on its own
    /// <see cref="CategoryInfo"/>. Defaults to <c>false</c>, matching every collection that reads no
    /// storage.
    /// </para>
    /// </remarks>
    public bool ReadsStorage { get; init; }
}
