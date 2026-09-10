using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>
/// The bestiary numbers the character is known to hold this login session, and how much of their
/// bestiary has been read.
/// </summary>
/// <remarks>
/// <para>
/// The bestiary window shows part of itself at a time, so facts arrive a page at a time while a
/// collection pass happens later and needs the whole set. This is the buffer between the two.
/// Every pass reports everything gathered rather than only what is new, which is what makes a
/// failed upload heal itself: the next sync carries what the failed one dropped, and the repeat
/// costs nothing against a server whose write is insert-only.
/// </para>
/// <para>
/// Held in memory and never persisted. What is known belongs to whoever is logged in, so both
/// session edges empty it — carrying a beast across a character switch would attribute one
/// character's collection to another. Nothing is lost by that: the bestiary lists every beast in
/// the game and marks the ones held, so paging through it again rebuilds the whole picture.
/// </para>
/// <para>
/// Every access is locked. The writers — the bestiary window's callback and the session-edge
/// clears — run on the game's main thread alongside the collection pass that reads, so the lock is
/// uncontended and costs nothing. It is there because that single thread is an assumption about
/// how the game dispatches, not something this class can enforce: should any caller ever arrive
/// from elsewhere, a set updated in steps can be left half-updated, and a read racing that update
/// can hand back a set that never existed.
/// </para>
/// </remarks>
public sealed class TamedBeastLedger
{
    // A set rather than a list: pages overlap when a player scrolls back, and the payload should
    // say how many beasts it is really reporting.
    private readonly HashSet<int> numbers = [];

    // Every bestiary number the window has been observed to list, held or not. Kept apart from the
    // held ones because it answers a different question: not "what does the character have" but
    // "how much of the bestiary has been looked at".
    private readonly HashSet<int> seen = [];

    // The bestiary's own tally of held-out-of-total, when it has been read.
    private int? capturedTotal;
    private int? beastTotal;

    // How many beasts the game's own data says the bestiary holds, which is a fact about the game
    // rather than about the window in front of the player.
    private int? domainSize;

    // The object every read and write below locks on. A private, dedicated instance rather than the
    // set itself or `this`, so no outside code can take the same lock and deadlock against it.
    private readonly object gate = new();

    /// <summary>How many distinct beasts the ledger is holding.</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return numbers.Count;
        }
    }

    /// <summary>Takes in one page of the character's own bestiary.</summary>
    /// <param name="page">What the bestiary window said while it was open.</param>
    /// <remarks>
    /// Pages accumulate: the window shows part of the bestiary at a time, so a complete picture is
    /// built from however many pages the player happens to look at. Nothing is ever removed by a
    /// later page — a beast held is held, and a page that does not mention it is simply a page
    /// about other beasts.
    /// </remarks>
    /// <returns>
    /// True when the page named a held beast this ledger did not already know, so the caller can
    /// act only on news. Answered as part of recording, so the question and the write cannot
    /// disagree.
    /// </returns>
    public bool RecordPage(BestiaryPageReading page)
    {
        lock (gate)
        {
            var heldBefore = numbers.Count;

            foreach (var number in page.Seen)
            {
                if (number > 0)
                    seen.Add(number);
            }

            // Held only counts when the same page also listed it. The completeness check leans on
            // every held beast being one of the seen ones — that is what lets "as many seen as the
            // bestiary has" mean the whole domain was covered — and requiring it here makes that a
            // property this class enforces rather than one it inherits from whoever calls it. The
            // loop order is what makes the test meaningful: seen is fully populated above.
            foreach (var number in page.Captured)
            {
                if (number > 0 && seen.Contains(number))
                    numbers.Add(number);
            }

            // The largest total ever seen wins, rather than the latest. A window showing a filtered
            // view reports a smaller bestiary than the game has, and letting that shrink the
            // remembered size would both invite a claim over the subset and revoke an honest claim
            // made before the filter went on.
            if (page.BeastTotal is > 0 && page.BeastTotal > (beastTotal ?? 0))
            {
                beastTotal = page.BeastTotal;
                capturedTotal = page.CapturedTotal;
            }
            else if (page.BeastTotal == beastTotal && page.CapturedTotal is not null)
            {
                // The same view of the bestiary, so its count of held beasts is the fresher one.
                capturedTotal = page.CapturedTotal;
            }

            return numbers.Count > heldBefore;
        }
    }

    /// <summary>
    /// Tells the ledger how many beasts the game's own data says the bestiary holds.
    /// </summary>
    /// <param name="size">The bestiary's real size, from the game's data sheet.</param>
    /// <remarks>
    /// The one figure in this class that does not come from the window. It is what stops a window
    /// showing part of the bestiary from being mistaken for the whole of it: a filtered view can
    /// report its own smaller total, and only something outside the window knows better.
    /// </remarks>
    public void NoteDomainSize(int size)
    {
        lock (gate)
        {
            if (size > 0)
                domainSize = size;
        }
    }

    /// <summary>
    /// Whether the whole bestiary has been accounted for, so that a beast's absence from this
    /// ledger is evidence the character does not hold it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three agreements are required, and they check each other.
    /// </para>
    /// <para>
    /// First, the window's idea of how many beasts exist must match the game's own — otherwise a
    /// window showing a filtered slice of the bestiary would supply both the numerator and the
    /// denominator, and a subset would agree with itself perfectly. This is the check that cannot
    /// come from the window, and without it a remembered filter turns a fragment into a claim.
    /// </para>
    /// <para>
    /// Second, every one of those numbers must actually have been seen. Third, the beasts recorded
    /// as held must match the count the window reports, which catches a page whose held flags were
    /// misread even when the right number of records was found.
    /// </para>
    /// <para>
    /// False until the player has looked at the whole bestiary, which is the honest answer: until
    /// then this holds a fragment, and a fragment must never be presented as the whole.
    /// </para>
    /// </remarks>
    public bool IsComplete
    {
        get
        {
            lock (gate)
            {
                return beastTotal is > 0
                    && capturedTotal is not null
                    && domainSize == beastTotal
                    && seen.Count == beastTotal
                    && numbers.Count == capturedTotal;
            }
        }
    }

    /// <summary>How many bestiary numbers have been seen, held or not.</summary>
    public int SeenCount
    {
        get
        {
            lock (gate)
                return seen.Count;
        }
    }

    /// <summary>How many beasts the bestiary says exist, or null when it has not been read.</summary>
    public int? BeastTotal
    {
        get
        {
            lock (gate)
                return beastTotal;
        }
    }

    /// <summary>
    /// Empties the ledger. Called at both edges of a login session, and whenever the collection is
    /// found switched off.
    /// </summary>
    public void Clear()
    {
        lock (gate)
        {
            numbers.Clear();
            seen.Clear();
            capturedTotal = null;
            beastTotal = null;

            // The bestiary's size is game data rather than character data, so it survives — the
            // next character's bestiary is the same size as this one's.
        }
    }

    /// <summary>Every beast recorded this session, ascending.</summary>
    /// <remarks>
    /// Sorted for the reader rather than for the server, which does not care about order: the
    /// upload log and a pasted diagnostic are easier to scan when the numbers are not in the order
    /// the player happened to tame them.
    /// </remarks>
    public IReadOnlyList<int> Snapshot()
    {
        List<int> snapshot;
        lock (gate)
            snapshot = new List<int>(numbers);

        // Sorted outside the lock: the copy is nobody else's now, so holding the gate through it
        // would only delay the next page the window hands over.
        snapshot.Sort();
        return snapshot;
    }
}
