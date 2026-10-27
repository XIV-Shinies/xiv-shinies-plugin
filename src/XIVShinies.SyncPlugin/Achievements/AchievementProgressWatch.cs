namespace XIVShinies.SyncPlugin.Achievements;

/// <summary>
/// Looks at the game's achievement progress fields each frame and says when they hold a server
/// answer not taken before.
/// </summary>
/// <remarks>
/// An answer is taken when the state turns <see cref="ProgressRequestState.Loaded"/>, or when the
/// values change while it stays there; never while a request waits, because the values are then the
/// previous answer's. Every look updates what has been seen, so an answer the caller drops is not
/// offered again later. The first look after a reset only learns what is there: the fields may hold
/// an answer from before the plugin loaded, or another character's.
/// </remarks>
public sealed class AchievementProgressWatch
{
    private ProgressFields? seen;

    /// <summary>Makes the next look a baseline.</summary>
    public void Reset() => seen = null;

    /// <summary>The fields, when they hold an answer not taken before; otherwise null.</summary>
    /// <param name="fields">The fields as they read this frame.</param>
    public ProgressFields? TakeNewAnswer(ProgressFields fields)
    {
        var previous = seen;
        seen = fields;

        if (previous is null || fields.State != ProgressRequestState.Loaded)
            return null;

        return previous == fields ? null : fields;
    }
}
