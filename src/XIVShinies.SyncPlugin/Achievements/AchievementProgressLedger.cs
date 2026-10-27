using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Achievements;

/// <summary>
/// The achievement progress readings taken this session, the newest per achievement. Uploads copy them,
/// and a reading leaves once the website confirms it stored it.
/// </summary>
/// <remarks>
/// Sending an unconfirmed reading again is safe: the server compares it with what it holds by its
/// observation time. Framework thread only, where the readings are taken and the collection pass reads
/// them.
/// </remarks>
public sealed class AchievementProgressLedger
{
    private readonly Dictionary<uint, AchievementProgressReading> byId = new();

    /// <summary>Holds the reading in place of any older one for the same achievement.</summary>
    /// <returns>False when the reading is outside the contract and was skipped.</returns>
    public bool Record(AchievementProgressReading reading)
    {
        if (!reading.IsWithinContract)
            return false;

        byId[reading.Id] = reading;
        return true;
    }

    /// <summary>A copy of every held reading.</summary>
    public IReadOnlyList<AchievementProgressReading> Snapshot() => [.. byId.Values];

    /// <summary>Drops each sent reading the website stored, unless a newer one has replaced it.</summary>
    public void DropStored(IEnumerable<AchievementProgressReading> sent)
    {
        foreach (var reading in sent)
        {
            if (byId.TryGetValue(reading.Id, out var held) && held == reading)
                byId.Remove(reading.Id);
        }
    }

    /// <summary>Drops every reading.</summary>
    public void Clear() => byId.Clear();
}
