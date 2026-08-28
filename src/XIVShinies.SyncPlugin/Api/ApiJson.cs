using System.Text.Json;
using System.Text.Json.Serialization;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The single JSON policy every API request and response is (de)serialized with. Centralizing it
/// means the wire format can't drift between call sites.
/// </summary>
/// <remarks>
/// One trap every DTO consumer inherits: <c>required</c> rejects an ABSENT field at
/// deserialization, but not a field set to an explicit JSON <c>null</c> — that lands as a null
/// reference despite the property's non-nullable type. The backend is user-overridable, so every
/// read of a required collection or string must survive a peer that sends null anyway.
/// </remarks>
public static class ApiJson
{
    /// <summary>Serializer settings matching the server contract exactly.</summary>
    // `static readonly` = one shared instance, assigned once. JsonSerializerOptions is expensive
    // to build and is safe to reuse across threads once it has been used, so a single shared
    // instance is the recommended pattern (a new one per call would tank performance).
    public static readonly JsonSerializerOptions Options = new()
    {
        // C# properties are PascalCase; the wire format is camelCase. This maps
        // CharacterContentIdHash -> characterContentIdHash automatically, both ways.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Dictionary KEYS are not property names, so the policy above does not touch them. Our
        // category keys are already lowercase ("quests"), making this a no-op today — it is set so
        // a future key written in PascalCase cannot silently reach the wire in the wrong case.
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,

        // THE monotonic-write rule, enforced at the serializer: a null property is left out of
        // the JSON entirely rather than written as `null`. That is what lets an unread category
        // be *absent* ("not read this time") instead of an empty array ("read, and it was empty").
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Writes enums as strings rather than their underlying integers, using the same camelCase
        // policy — so SyncTrigger.Login becomes "login", which is what the contract specifies.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
