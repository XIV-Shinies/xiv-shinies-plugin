using System.Collections.Generic;
using System.Linq;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>Picks the store confirmations an upload's response bears out.</summary>
public static class StoreConfirmations
{
    /// <summary>The confirmations whose key the response carries; none without a body.</summary>
    public static IReadOnlyList<StoreConfirmation> Confirmed(
        IEnumerable<StoreConfirmation> pending, SyncResponse? response) =>
        response is null
            ? []
            : [.. pending.Where(confirmation => response.Carries(confirmation.ResponseKey))];
}
