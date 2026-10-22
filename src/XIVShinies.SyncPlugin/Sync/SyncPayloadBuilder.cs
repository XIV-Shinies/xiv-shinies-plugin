using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Turns one collection pass into the body of a sync upload.
/// </summary>
/// <remarks>
/// Reports exactly the categories the snapshot read and invents nothing. A skipped category is absent,
/// which the server reads as "not read this time" rather than "empty", so a partial upload never
/// erases anything.
/// </remarks>
public static class SyncPayloadBuilder
{
    /// <summary>The longest manifestVersion the contract accepts on an upload.</summary>
    public const int MaxManifestVersionLength = 100;

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
        // An over-long echo is dropped, not shortened: the field is optional, the full value would earn
        // a 400 on every upload, and a cut one would be a different wrong value.
        if (manifestVersion is { Length: > MaxManifestVersionLength })
            manifestVersion = null;

        return new SyncRequest
        {
            CharacterContentIdHash = identity.ContentIdHash,

            // The contract trims the name but length-checks the world as sent. Trimming both sends what
            // CharacterIdentity.IsUsable checked, so a padded world cannot fail the whole upload.
            CharacterName = identity.Name.Trim(),
            HomeWorld = identity.HomeWorld.Trim(),

            PluginVersion = pluginVersion,
            Trigger = trigger,
            ManifestVersion = manifestVersion,

            Collections = snapshot.Collections,

            CollectionScopes = BuildCollectionScopes(snapshot, trigger),

            // Null rather than an empty object when nothing is worth sending, since ApiJson.Options
            // omits null properties.
            ItemSources = BuildWireSourceNotes(snapshot.SourceNotes),
        };
    }

    /// <summary>
    /// The completeness declarations that belong on the wire, or null when none do.
    /// </summary>
    /// <remarks>
    /// Only <c>"full"</c> is emitted: an omitted key already means "partial", and the
    /// minimize-what-you-send rule forbids bytes that say nothing. An <c>unlock</c> upload declares
    /// nothing, whatever it read, because the contract asks a delta upload for "partial" and the server
    /// would trust a "full" on one. A scope is never built for a list the payload does not carry, and
    /// <see cref="PayloadCaps"/> retracts the claim of a list cut after collection.
    /// </remarks>
    private static Dictionary<string, string>? BuildCollectionScopes(
        CollectionSnapshot snapshot, SyncTrigger trigger)
    {
        if (trigger == SyncTrigger.Unlock)
            return null;

        // Null until a declaration survives the checks, so "none" becomes an omitted key.
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
    /// Source status makes counts judgeable, so a <see cref="SourceStates.Unreadable"/> source, which
    /// the game never exposes and which never carries counts, is dropped as noise under the
    /// minimize-what-you-send rule; the settings panel still shows it. Keyed on the state, never on a
    /// source name, so any unreadable source stays local.
    /// </remarks>
    private static Dictionary<string, ItemSourceStatus>? BuildWireSourceNotes(
        IReadOnlyDictionary<string, ItemSourceStatus> sourceNotes)
    {
        // Null until a wire-worthy note appears, so no notes and only local notes both omit the key.
        Dictionary<string, ItemSourceStatus>? wireNotes = null;

        foreach (var (sourceKey, status) in sourceNotes)
        {
            if (status.State == SourceStates.Unreadable)
                continue;

            wireNotes ??= new Dictionary<string, ItemSourceStatus>();
            wireNotes[sourceKey] = status;
        }

        return wireNotes;
    }
}
