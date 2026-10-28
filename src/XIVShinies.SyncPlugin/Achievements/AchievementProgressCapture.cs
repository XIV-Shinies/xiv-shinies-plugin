using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Achievements;

/// <summary>What one look at the progress fields found.</summary>
public enum ProgressLook
{
    /// <summary>No answer landed since the last look.</summary>
    NoAnswer,

    /// <summary>An answer landed and is held for the next sync.</summary>
    Taken,

    /// <summary>An answer landed while it did not count, and is dropped.</summary>
    NotCounted,

    /// <summary>An answer landed with a value the contract refuses, and is skipped.</summary>
    OutsideContract,
}

/// <summary>A look's outcome, and the answer it was about.</summary>
/// <param name="Look">What the look found.</param>
/// <param name="Answer">
/// The fields that held the answer; meaningless for <see cref="ProgressLook.NoAnswer"/>.
/// </param>
public readonly record struct ProgressLookResult(ProgressLook Look, ProgressFields Answer);

/// <summary>
/// Turns the game's progress fields, looked at once a frame, into the readings held for the next sync.
/// </summary>
/// <remarks>Framework thread only, where the fields are read and the collection pass runs.</remarks>
public sealed class AchievementProgressCapture
{
    private readonly AchievementProgressWatch watch = new();
    private readonly AchievementProgressLedger ledger = new();

    /// <summary>The held readings, the newest per achievement.</summary>
    public IReadOnlyList<AchievementProgressReading> Readings() => ledger.Snapshot();

    /// <summary>Drops each sent reading the website stored, unless a newer one has replaced it.</summary>
    public void DropStored(IReadOnlyList<AchievementProgressReading> sent) => ledger.DropStored(sent);

    /// <summary>Drops every reading and makes the next look a baseline.</summary>
    public void Forget()
    {
        ledger.Clear();
        watch.Reset();
    }

    /// <summary>Looks at the fields once, and holds an answer that landed if it counts.</summary>
    /// <param name="fields">The fields as they read this frame.</param>
    /// <param name="answerCounts">
    /// Whether an answer landing now counts. Asked only when one lands, since it looks up a game
    /// window.
    /// </param>
    /// <param name="now">The moment the reading is stamped with.</param>
    public ProgressLookResult Look(ProgressFields fields, Func<bool> answerCounts, DateTimeOffset now)
    {
        if (watch.TakeNewAnswer(fields) is not { } answer)
            return new(ProgressLook.NoAnswer, fields);

        if (!answerCounts())
            return new(ProgressLook.NotCounted, answer);

        var reading = new AchievementProgressReading
        {
            Id = answer.AchievementId,
            Current = answer.Current,
            Max = answer.Max,
            ObservedAt = now,
        };

        return ledger.Record(reading)
            ? new(ProgressLook.Taken, answer)
            : new(ProgressLook.OutsideContract, answer);
    }
}
