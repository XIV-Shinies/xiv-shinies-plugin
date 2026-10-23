using System;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// A "Sync now" press made while the website has syncing switched off. The plugin asks
/// <c>/config</c> once for it, and syncs if the answer says syncing is back on.
/// </summary>
/// <remarks>
/// A sweep during a pause would be refused at the upload gate, so the press asks the website instead,
/// and a player who has just turned "Sync from the plugin" back on resumes without waiting for the next
/// scheduled poll. Only a poll that started after the latest press answers it, because one already on
/// its way answers for the moment before; polls are numbered as they start (see <c>SyncManager</c>).
/// Presses start at most one poll per <see cref="PollFloor"/>, however fast the button is pressed, and
/// a press inside that gap waits rather than being dropped.
/// </remarks>
public sealed class PausedPress
{
    /// <summary>The shortest gap between two polls that presses ask for.</summary>
    public static readonly TimeSpan PollFloor = TimeSpan.FromSeconds(10);

    /// <summary>Guards every field below: the press and the tick write, the poll task settles.</summary>
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
        lock (gate)
            pollsStartedAtPress = pollsStarted;
    }

    /// <summary>True when a poll should start for the waiting press now.</summary>
    /// <param name="pollsStarted">How many polls have started so far.</param>
    /// <param name="pollInFlight">Whether a poll is on its way.</param>
    /// <param name="now">The current moment.</param>
    /// <remarks>
    /// Not while another poll is on its way, since only one goes at a time, and not once a poll has
    /// started since the press, because that poll answers it.
    /// </remarks>
    public bool PollDue(int pollsStarted, bool pollInFlight, DateTimeOffset now)
    {
        lock (gate)
        {
            // A clock that jumped backwards would hold the poll off by the jump.
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
    /// <returns>True to sweep: the poll answered the press, and syncing is back on.</returns>
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
