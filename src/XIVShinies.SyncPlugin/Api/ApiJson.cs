using System.Text.Json;
using System.Text.Json.Serialization;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The one JSON policy for every API request and response, so the wire format cannot drift between
/// call sites.
/// </summary>
/// <remarks>
/// <c>required</c> rejects an absent field but not an explicit JSON <c>null</c>, which lands as a null
/// reference despite the non-nullable type. The backend is user-overridable, so every read of a
/// required collection or string must survive a null.
/// </remarks>
public static class ApiJson
{
    /// <summary>Serializer settings matching the server contract exactly.</summary>
    // Shared: options are expensive to build, and a used instance is safe across threads.
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // The naming policy skips dictionary keys. Category keys are already lowercase; this keeps a
        // PascalCase key from reaching the wire.
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,

        // The monotonic-write rule: a null property is left out, so an unread category is absent
        // ("not read this time"), never an empty array ("read, and it was empty").
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Enums go out as camelCase strings, as the contract specifies: SyncTrigger.Login is "login".
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
