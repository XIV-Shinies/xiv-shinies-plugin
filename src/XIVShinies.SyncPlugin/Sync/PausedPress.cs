using System;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// A "Sync now" press made while the website has syncing switched off. The plugin asks
/// <c>/config</c> once for it, and syncs if the answer says syncing is back on.
/// </summary>
/// <remarks>
/// <para>
/// A sweep made during a pause would be refused at the upload gate, so the press asks the website
/// instead. This is how a player who has just turned "Sync from the plugin" back on resumes without
/// waiting for the next scheduled poll.
/// </para>
/// <para>
/// Only a poll that started after the latest press answers it, because one already on its way
/// answers for the moment before. Polls are numbered as they start (see <c>SyncManager</c>), and the
/// press remembers how many had started when it was made. Press-driven polls go at most once per
/// <see cref="PollFloor"/>, however fast the button is pressed; a press inside that gap waits for
/// it rather than being dropped.
/// </para>
/// </remarks>
public sealed class PausedPress
{
    /// <summary>The shortest gap between two polls that presses ask for.</summary>
    // `static readonly` rather than `const`: a TimeSpan is built when the program runs.
    public static readonly TimeSpan PollFloor = TimeSpan.FromSeconds(10);

    /// <summary>Guards every field below: the press and the tick write, the poll task settles.</summary>
    /// <remarks>
    /// <c>lock (gate) { … }</c> lets one thread at a time into the block; see
    /// <see cref="SyncResumeWait"/> for the longer note.
    /// </remarks>
    private readonly object gate = new();

    /// <summary>
    /// How many polls had started when the waiting press was made, or null when no press waits.
    /// </summary>
    private int? pollsStartedAtPress;

    /// <summary>The earliest moment a press may start another poll.</summary>
    private DateTimeOffset nextPollAllowedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Records a press. A press made while one waits takes its place, so only a poll started after the
    /// latest press answers: the player may have turned the switch back on between the two.
    /// </summary>
    /// <param name="pollsStarted">How many <c>/config</c> polls had started when it was made.</param>
    public void Press(int pollsStarted)
    {
        // `lock` with no braces guards the single statement after it.
        lock (gate)
            pollsStartedAtPress = pollsStarted;
    }

    /// <summary>Whether to start a poll for the waiting press now.</summary>
    /// <param name="pollsStarted">How many polls have started so far.</param>
    /// <param name="pollInFlight">Whether a poll is on its way.</param>
    /// <param name="now">The current moment.</param>
    /// <remarks>
    /// Not while another poll is on its way (only one goes at a time), and not once a poll has started
    /// since the press, because that poll answers it.
    /// </remarks>
    public bool PollDue(int pollsStarted, bool pollInFlight, DateTimeOffset now)
    {
        lock (gate)
        {
            // A clock that jumped backwards would otherwise hold the poll off by however far it jumped.
            if (nextPollAllowedAt > now + PollFloor)
                nextPollAllowedAt = now;

            return pollsStartedAtPress is { } atPress
                && pollsStarted == atPress
                && !pollInFlight
                && now >= nextPollAllowedAt;
        }
    }

    /// <summary>Records that a poll started for the waiting press, so the next one waits the floor.</summary>
    /// <param name="now">The current moment.</param>
    public void PollStarted(DateTimeOffset now)
    {
        lock (gate)
            nextPollAllowedAt = now + PollFloor;
    }

    /// <summary>
    /// Settles the waiting press with a finished poll's answer, if that poll answers it.
    /// </summary>
    /// <param name="pollNumber">The finished poll's number.</param>
    /// <param name="syncEnabled">
    /// The answer's <c>enabled</c>, or null when the poll brought no config back.
    /// </param>
    /// <returns>Whether to sweep: the poll answered the press, and syncing is back on.</returns>
    /// <remarks>
    /// A poll that brought no config back still settles the press: the player can press again, and
    /// asking again on its own would keep polling a website that cannot be reached or is refusing.
    /// </remarks>
    public bool Settle(int pollNumber, bool? syncEnabled)
    {
        lock (gate)
        {
            if (pollsStartedAtPress is not { } atPress || pollNumber <= atPress)
                return false;

            pollsStartedAtPress = null;
            return syncEnabled == true;
        }
    }
}
