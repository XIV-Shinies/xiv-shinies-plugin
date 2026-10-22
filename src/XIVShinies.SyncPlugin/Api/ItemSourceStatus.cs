namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// How one storage source was read during an item scan, such as the inventory, currencies or retainers.
/// </summary>
public sealed record ItemSourceStatus
{
    /// <summary>One of the <see cref="SourceStates"/> wire strings.</summary>
    public required string State { get; init; }

    /// <summary>
    /// Source-specific detail, omitted from JSON when null. For retainers, how many retainers'
    /// contents were cached rather than live; unused for other sources.
    /// </summary>
    public int? Count { get; init; }

    /// <summary>
    /// How many of the source exist, when the game can say; omitted from JSON when null. For
    /// retainers, the retainer count, so a reader can tell "3 of 5 scanned" from "3 of 3".
    /// </summary>
    /// <remarks>A count only: nothing that identifies an individual retainer travels.</remarks>
    public int? Total { get; init; }
}

/// <summary>The wire strings for <see cref="ItemSourceStatus.State"/>.</summary>
public static class SourceStates
{
    /// <summary>Read directly from game memory this pass.</summary>
    public const string Live = "live";

    /// <summary>
    /// Read from the game's local cache, filled when the player last opened the source: real but
    /// possibly stale.
    /// </summary>
    public const string Cached = "cached";

    /// <summary>
    /// The player has never opened the source, so its cache is empty and it contributes no counts.
    /// This status tells the server a low total may be a floor rather than the truth.
    /// </summary>
    public const string Unscanned = "unscanned";

    /// <summary>
    /// The armoire's contents are loaded and readable. The game fetches them from the server the
    /// first time the player opens the armoire each session.
    /// </summary>
    public const string Loaded = "loaded";

    /// <summary>
    /// The game never exposes this source to plugins, and no player action changes that; opening the
    /// source once resolves <see cref="Unscanned"/>, but not this.
    /// </summary>
    /// <remarks>
    /// Local-only: the settings window uses it to say why the source is absent from the counts, and
    /// <see cref="Sync.SyncPayloadBuilder"/> drops it from the upload, since a source that can never
    /// carry counts tells the server nothing.
    /// </remarks>
    public const string Unreadable = "unreadable";
}

/// <summary>The wire keys naming each storage source.</summary>
/// <remarks>
/// Source-keyed, not category-keyed: any collector may report on any source, and nothing downstream
/// branches on which collector said it. The values are already camelCase, so <see cref="ApiJson"/>'s
/// dictionary-key policy leaves them as written.
/// </remarks>
public static class SourceKeys
{
    /// <summary>
    /// The containers read live each pass: bags, equipped gear, the armoury chest, and crystals.
    /// </summary>
    public const string Inventory = "inventory";

    /// <summary>The character's currencies, read live each pass.</summary>
    /// <remarks>
    /// Both places the game keeps them are read: the Currency inventory container, and the currency
    /// subsystem that tracks the rest, such as scrips, Bicolor Gemstones and ventures.
    /// </remarks>
    public const string Currencies = "currencies";

    /// <summary>The chocobo saddlebag, including the premium half.</summary>
    public const string Saddlebag = "saddlebag";

    /// <summary>The player's own retainers: their bags and equipped gear.</summary>
    public const string Retainers = "retainers";

    /// <summary>The armoire (cabinet): long-term storage for select gear the game curates.</summary>
    public const string Armoire = "armoire";

    /// <summary>The glamour dresser: gear stored away for glamour use.</summary>
    public const string GlamourDresser = "glamourDresser";

    /// <summary>Housing mannequins. Always <see cref="SourceStates.Unreadable"/>.</summary>
    /// <remarks>
    /// The game fetches a mannequin's contents only while the player interacts with it in the house,
    /// and never caches them. The key exists so the settings panel can say so; it never travels.
    /// </remarks>
    public const string Mannequins = "mannequins";
}
