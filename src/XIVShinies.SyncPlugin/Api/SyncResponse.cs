using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The 200 body of <c>POST /api/plugin/v1/sync</c>.
/// </summary>
/// <remarks>
/// The server omits optional keys rather than sending them as null, so the plugin can
/// feature-detect them. That is why they are nullable here: null means "the server did not send
/// this", which is exactly the signal we want.
/// </remarks>
public sealed record SyncResponse
{
    /// <summary>Always true on a 200.</summary>
    public required bool Ok { get; init; }

    /// <summary>True only when THIS request performed the first-upload character bind.</summary>
    public required bool Bound { get; init; }

    /// <summary>
    /// Rows created plus promoted, keyed by category. <c>items</c> never appears — item possession
    /// feeds relic proofs rather than a collection count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dictionary rather than one property per category, for the same reason
    /// <see cref="SyncRequest.Collections"/> is one: a property per collection would mean adding a
    /// collection touches this file too, breaking the rule that adding one is a single new
    /// collector class and nothing else. No category's presence is part of this type's contract, so
    /// a server generation that names different categories simply deserializes.
    /// </para>
    /// <para>
    /// Informational only: <b>no plugin logic may branch on these.</b> Reading one by name would
    /// reintroduce exactly the category-name dependency the rest of the plugin avoids. What the
    /// upload log shows is what was SENT, summarized from the snapshot — never this.
    /// </para>
    /// <para>
    /// Not <c>required</c>: an empty dictionary is the honest reading of a response that omitted
    /// the object, and refusing to deserialize over an informational field would turn a successful
    /// upload into a reported failure.
    /// </para>
    /// </remarks>
    public Dictionary<string, int> Written { get; init; } = new();

    /// <summary>
    /// Present only when the achievements key was absent or stripped as disabled. An explicit
    /// empty array counts as "sent" and gets no flag.
    /// </summary>
    public string? AchievementsSkipped { get; init; }

    /// <summary>
    /// Present only when items were applied and relic-proof derivation succeeded. Absent on an
    /// items-carrying upload means derivation failed server-side; the next upload retries it.
    /// </summary>
    public int? ProvenSteps { get; init; }

    /// <summary>
    /// Present only when the server stripped disabled categories from this payload. Lets the
    /// plugin tell the user why a category did not sync.
    /// </summary>
    public IReadOnlyList<string>? SkippedCategories { get; init; }
}

