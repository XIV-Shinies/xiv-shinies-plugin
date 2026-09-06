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
    /// Self-description in the same sense as <see cref="UsesItemManifest"/>: the collectors read
    /// this flag rather than deciding for themselves, so the decision sits beside the category it
    /// describes and one test can pin the whole set. Declaring it is a request, not a guarantee:
    /// <see cref="CollectResult.CompleteEnumeration"/> describes the floor applied at the factory,
    /// and further gates on <c>PayloadCaps</c> and <c>SyncPayloadBuilder</c> can withhold it later
    /// still. Defaults to <c>false</c>, which is always safe: it withholds a claim rather than
    /// making a wrong one.
    /// </para>
    /// </remarks>
    public bool EnumeratesCompleteDomain { get; init; }
}
