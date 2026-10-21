using System;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// One upload path's wait after a 503 saying syncing is switched off: whether the website saying
/// syncing is back on may end that wait before its deadline, and the moment it has.
/// </summary>
/// <remarks>
/// <para>
/// The collections upload, the live tracker and the Crucible run sharing each keep one, because
/// each holds its own wait and each answers to its own switch on <c>/config</c>. A path arms it when
/// the refusal arrives, on its upload task, before it sets its scheduler's wait, and asks it on every
/// tick whether the wait is over. Only a poll that starts after the arming counts, so a tick could
/// end the wait before the scheduler holds it only if that poll made its whole round trip between
/// those two statements.
/// </para>
/// <para>
/// The wait ends early only on a <c>/config</c> poll that started after the refusal (see
/// <see cref="RetryPolicy.ResumesSync"/>) and that says the path's own switch is on. A refusal that
/// arrives inside a wait this ended early, with no upload accepted in between, may not end early at
/// all: a website whose <c>/config</c> says syncing is on while its uploads say it is off would
/// otherwise turn its hour's wait into a few minutes, over and over, so its own wait is honored
/// instead. An accepted upload (see <see cref="UploadAccepted"/>) shows the early end was right, so a
/// player who switches syncing off again within the hour still resumes as soon as it is back on.
/// </para>
/// </remarks>
public sealed class SyncResumeWait
{
    /// <summary>Guards every field below: the upload task arms, the tick asks.</summary>
    /// <remarks>
    /// <c>lock (gate) { … }</c> lets one thread at a time into the block. Any object works as the
    /// key; a private one nobody else can lock on is the convention. JavaScript has no equivalent,
    /// because its runtime runs one thread.
    /// </remarks>
    private readonly object gate = new();

    /// <summary>
    /// How many polls had started when the refusal behind the current wait arrived, or null when no
    /// wait is waiting on the website.
    /// </summary>
    private int? pollsStartedAtRefusal;

    /// <summary>When the current wait ends on its own.</summary>
    private DateTimeOffset waitEndsAt;

    /// <summary>When the wait this last ended early would have ended on its own, or null.</summary>
    private DateTimeOffset? endedEarlyWaitEndsAt;

    /// <summary>
    /// Records a refusal, and says whether its wait may end before <paramref name="until"/>.
    /// </summary>
    /// <param name="pollsStarted">How many <c>/config</c> polls had started when it arrived.</param>
    /// <param name="now">The current moment.</param>
    /// <param name="until">When its wait ends on its own.</param>
    /// <returns>
    /// False when it arrives inside a wait this ended early, with no upload accepted since (see
    /// <see cref="UploadAccepted"/>); the caller then waits until <paramref name="until"/> like any
    /// other wait.
    /// </returns>
    public bool Arm(int pollsStarted, DateTimeOffset now, DateTimeOffset until)
    {
        lock (gate)
        {
            if (endedEarlyWaitEndsAt is { } earlier && now < earlier)
                return false;

            // A second refusal raises the bar to the polls started by then, and the wait runs until
            // the later of the two deadlines, as the scheduler holding it does.
            if (pollsStartedAtRefusal is null || until > waitEndsAt)
                waitEndsAt = until;

            pollsStartedAtRefusal = pollsStarted;
            return true;
        }
    }

    /// <summary>
    /// Whether the latest <c>/config</c> answer ends the wait now. True at most once per wait, so the
    /// caller ends its scheduler's wait once.
    /// </summary>
    /// <param name="answeredPoll">The number of the poll the latest <c>/config</c> came from.</param>
    /// <param name="switchOn">Whether that answer says this path's own switch is on.</param>
    /// <param name="now">The current moment.</param>
    /// <remarks>
    /// A wait that has already run out is let go without counting as ended early, because nothing
    /// is holding uploads back any more.
    /// </remarks>
    public bool TryEnd(int answeredPoll, bool switchOn, DateTimeOffset now)
    {
        lock (gate)
        {
            if (pollsStartedAtRefusal is not { } refused)
                return false;

            if (now >= waitEndsAt)
            {
                pollsStartedAtRefusal = null;
                return false;
            }

            if (!RetryPolicy.ResumesSync(switchOn, answeredPoll, refused))
                return false;

            pollsStartedAtRefusal = null;
            endedEarlyWaitEndsAt = waitEndsAt;
            return true;
        }
    }

    /// <summary>
    /// Records that the website accepted an upload on this path, so the next refusal may end early
    /// again even inside the wait this last ended early.
    /// </summary>
    public void UploadAccepted()
    {
        // `lock` with no braces guards the single statement after it.
        lock (gate)
            endedEarlyWaitEndsAt = null;
    }
}
