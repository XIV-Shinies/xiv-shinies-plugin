using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Turns one collection pass into the body of a sync upload.
/// </summary>
/// <remarks>
/// Deliberately dumb. It reports exactly the categories the snapshot read — no more, no fewer — and
/// invents nothing. A category the collectors skipped is simply absent, which the server reads as
/// "not read this time" rather than "empty", so a partial upload can never erase anything.
/// </remarks>
public static class SyncPayloadBuilder
{
    /// <summary>Builds the request body.</summary>
    /// <param name="identity">The local character, already hashed.</param>
    /// <param name="pluginVersion">This plugin's version.</param>
    /// <param name="trigger">What prompted the upload.</param>
    /// <param name="snapshot">What the collectors read.</param>
    /// <param name="manifestVersion">
    /// The <c>/config</c> manifest version the item list was built against, echoed back so the
    /// server can record it. Null when no config has been fetched, and then omitted from the JSON.
    /// </param>
    public static SyncRequest Build(
        CharacterIdentity identity,
        string pluginVersion,
        SyncTrigger trigger,
        CollectionSnapshot snapshot,
        string? manifestVersion)
    {
        return new SyncRequest
        {
            CharacterContentIdHash = identity.ContentIdHash,

            // The server trims and length-checks these; sending untrimmed input would fail
            // validation and take the whole upload down with it.
            CharacterName = identity.Name.Trim(),
            HomeWorld = identity.HomeWorld.Trim(),

            PluginVersion = pluginVersion,
            Trigger = trigger,
            ManifestVersion = manifestVersion,

            // Handed straight through. Whichever categories the collectors read, and only those.
            Collections = snapshot.Collections,

            // Which of those lists are complete sets, per the collectors' own claims. Null (an
            // omitted key, meaning "all partial") when there is nothing to declare.
            CollectionScopes = BuildCollectionScopes(snapshot, trigger),

            // Per-source scan status, or null when there is nothing to send. An empty object on
            // the wire is noise when no source status is worth reporting; null makes the shared
            // serializer policy (ApiJson.Options omits null properties) drop the key entirely.
            ItemSources = BuildWireSourceNotes(snapshot.SourceNotes),
        };
    }

    /// <summary>
    /// The completeness declarations that belong on the wire, or null when none do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <c>"full"</c> is ever emitted: an omitted key already means "partial" on the server,
    /// so writing <c>"partial"</c> out would say nothing at the cost of bytes — the same
    /// minimize-what-you-send reasoning that drops unreadable source notes below.
    /// </para>
    /// <para>
    /// An <c>unlock</c> upload declares nothing, whatever its collectors read. The contract asks
    /// a delta upload to report <c>"partial"</c>, and the server would accept a <c>"full"</c> it
    /// sent — it is not schema-rejected — so this is the plugin refusing to make a claim the
    /// server would trust. An unlock upload exists to date the one thing that just changed; it
    /// carries whichever categories the unlock routed to and must never be read as a statement
    /// about everything the character owns. The gate is the trigger's, not any category's.
    /// </para>
    /// <para>
    /// Intersected with the carried categories even though the runner only marks keys it
    /// collected — a scope for a list this upload does not contain would assert completeness of
    /// facts the server never received, so the builder refuses to construct one no matter what a
    /// future snapshot source claims. What it cannot see is a list that was cut AFTER collection:
    /// <see cref="PayloadCaps"/> owns that, retracting the claim of any category it truncates.
    /// </para>
    /// </remarks>
    private static Dictionary<string, string>? BuildCollectionScopes(
        CollectionSnapshot snapshot, SyncTrigger trigger)
    {
        if (trigger == SyncTrigger.Unlock)
            return null;

        // Null until a declaration survives the checks, so "none" becomes an omitted key without
        // a second emptiness check — the same shape as BuildWireSourceNotes below.
        Dictionary<string, string>? scopes = null;

        foreach (var categoryKey in snapshot.CompleteKeys)
        {
            if (!snapshot.Collections.ContainsKey(categoryKey))
                continue;

            scopes ??= new Dictionary<string, string>();
            scopes[categoryKey] = CollectionScopeValues.Full;
        }

        return scopes;
    }

    /// <summary>
    /// The source notes that belong on the wire, or null when none do.
    /// </summary>
    /// <remarks>
    /// Source status exists to make counts judgeable — "was the saddlebag ever scanned?" — so a
    /// source in the <see cref="SourceStates.Unreadable"/> state is dropped here: the game never
    /// exposes it, it can never carry counts, and repeating that constant on every upload would be
    /// exactly the noise the minimize-what-you-send rule forbids. The state is kept for the
    /// settings panel, which is where a user wondering about such a source actually looks. Keyed
    /// on the STATE, never on a source name, so any future unreadable source stays local the same
    /// way.
    /// </remarks>
    private static Dictionary<string, ItemSourceStatus>? BuildWireSourceNotes(
        IReadOnlyDictionary<string, ItemSourceStatus> sourceNotes)
    {
        // Null until something wire-worthy appears, so the "no notes at all" and the "only local
        // notes" cases converge on the same omitted key without a second emptiness check.
        Dictionary<string, ItemSourceStatus>? wireNotes = null;

        foreach (var (sourceKey, status) in sourceNotes)
        {
            if (status.State == SourceStates.Unreadable)
                continue;

            // `??=` assigns only when the left side is still null — the dictionary is created on
            // the first wire-worthy note and reused for the rest.
            wireNotes ??= new Dictionary<string, ItemSourceStatus>();
            wireNotes[sourceKey] = status;
        }

        return wireNotes;
    }
}
