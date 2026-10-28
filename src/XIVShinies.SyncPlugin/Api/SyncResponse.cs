using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The 200 body of <c>POST /api/plugin/v1/sync</c>.
/// </summary>
/// <remarks>
/// The server omits optional keys rather than sending null, so a null here means "not sent", which is
/// how the plugin feature-detects them.
/// </remarks>
public sealed record SyncResponse
{
    /// <summary>Always true on a 200.</summary>
    public required bool Ok { get; init; }

    /// <summary>True only when THIS request performed the first-upload character bind.</summary>
    public required bool Bound { get; init; }

    /// <summary>
    /// Rows created plus promoted, keyed by category. <c>items</c> never appears, since item
    /// possession feeds relic proofs rather than a collection count.
    /// </summary>
    /// <remarks>
    /// Informational only: <b>no plugin logic may branch on these</b>, and the upload log shows what
    /// was sent, never this. A dictionary, so a new collection changes nothing here. Not
    /// <c>required</c>: a missing object reads as empty rather than turning a successful upload into a
    /// failure.
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
    /// Present only when the server stripped disabled categories from this payload, so the plugin can
    /// tell the user why a category did not sync.
    /// </summary>
    public IReadOnlyList<string>? SkippedCategories { get; init; }

    /// <summary>The top-level keys no property here models, such as a store confirmation.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmodeledKeys { get; init; }

    /// <summary>Whether the response carries the key, among those no property here models.</summary>
    /// <remarks>A null counts as absent, matching how the server omits a key it does not send.</remarks>
    public bool Carries(string key) =>
        UnmodeledKeys is { } keys
        && keys.TryGetValue(key, out var value)
        && value.ValueKind != JsonValueKind.Null;
}

