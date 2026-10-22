namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// The category keys the server contract defines. A collector announces one as its
/// <see cref="ICollector.CategoryKey"/>; the same string keys the user's opt-in, the server's kill
/// switch and the payload.
/// </summary>
/// <remarks>
/// Not a category-name branch: only the collectors read these, to say what they are.
/// </remarks>
public static class CategoryKeys
{
    /// <summary>Completed quest IDs.</summary>
    public const string Quests = "quests";

    /// <summary>Completed achievement IDs.</summary>
    public const string Achievements = "achievements";

    /// <summary>Unlocked mount IDs.</summary>
    public const string Mounts = "mounts";

    /// <summary>Unlocked minion (Companion) IDs.</summary>
    public const string Minions = "minions";

    /// <summary>Possession counts for the items the server asked about.</summary>
    public const string Items = "items";

    /// <summary>Journal sequence positions for the quests the server asked about.</summary>
    public const string QuestSequences = "questSequences";

    /// <summary>Unlocked orchestrion roll IDs.</summary>
    public const string OrchestrionRolls = "orchestrionRolls";

    /// <summary>Unlocked Triple Triad card IDs.</summary>
    public const string TripleTriadCards = "tripleTriadCards";

    /// <summary>Defeated Triple Triad NPC IDs.</summary>
    public const string TripleTriadNpcs = "tripleTriadNpcs";

    /// <summary>Phantom job levels and EXP, and the optional knowledge-level sighting.</summary>
    public const string OccultProgression = "occultProgression";

    /// <summary>Discovered occult record IDs.</summary>
    public const string OccultRecords = "occultRecords";

    /// <summary>Bestiary numbers of the beasts a pact has been forged with.</summary>
    public const string TamedBeasts = "tamedBeasts";
}
