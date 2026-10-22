using System.Collections.Generic;
using System.Linq;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>What a ledger holds at one moment, and whether that adds up to the whole bestiary.</summary>
/// <param name="Held">How many beasts the bestiary window has shown as held.</param>
/// <param name="Seen">How many bestiary numbers have been listed to it, held or not.</param>
/// <param name="CapturedTotal">The window's own count of held beasts, or null when unread.</param>
/// <param name="BeastTotal">The window's own count of beasts that exist, or null when unread.</param>
/// <param name="IsComplete">Whether the whole bestiary has been accounted for.</param>
public readonly record struct LedgerReport(
    int Held,
    int Seen,
    int? CapturedTotal,
    int? BeastTotal,
    bool IsComplete);

/// <summary>
/// The bestiary numbers the character is known to hold this login session, how much of the bestiary
/// has been read, and each beast's rank once read.
/// </summary>
/// <remarks>
/// The window shows a page at a time while a collection pass later needs the whole set, so this
/// buffers between them. Every pass reports everything gathered, so a failed upload heals on the next
/// sync, at no cost against the server's insert-only write. It lives in memory only and is emptied at
/// both session edges, so one character's beasts are never attributed to another. Paging through the
/// bestiary rebuilds the held set, and talking to the Crucible's NPC rebuilds the ranks. Every access
/// is locked: the writers and the collection pass all run on the game's main thread, but that is an
/// assumption about the game's dispatch this class cannot enforce, and a racing read could see a
/// half-updated set.
/// </remarks>
public sealed class TamedBeastLedger
{
    // A set: pages overlap when a player scrolls back, and the payload should say how many beasts it
    // really reports.
    private readonly HashSet<int> numbers = [];

    // Every number the window has listed, held or not: how much of the bestiary has been looked at.
    private readonly HashSet<int> seen = [];

    // The window's own held-out-of-total tally, once read.
    private int? capturedTotal;
    private int? beastTotal;

    // The bestiary's size from the game's data rather than the window (see IsComplete).
    private int? domainSize;

    // Each beast's rank from the last reading of the record set. Kept apart from the held numbers,
    // which the completeness test counts and so must come from the window alone.
    private Dictionary<int, int> ranks = new();

    // The beasts that reading shows as raised past a fresh pact, and so held (see
    // BeastRankReading.Leveled).
    private HashSet<int> leveled = [];

    private readonly object gate = new();

    /// <summary>How many distinct beasts the bestiary window has shown as held.</summary>
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
    /// Pages accumulate, and a later page never removes anything: a page that does not mention a beast
    /// is about other beasts.
    /// </remarks>
    /// <returns>
    /// True when the page named a held beast the ledger did not know, so the caller acts only on news.
    /// Answered while recording, so the answer and the write cannot disagree.
    /// </returns>
    public bool RecordPage(BestiaryPageReading page)
    {
        lock (gate)
        {
            var heldBefore = numbers.Count;

            // Bounded by the game's bestiary size when known, so a number past the sheet's end cannot
            // make the count add up by accident.
            foreach (var number in page.Seen)
            {
                if (number > 0 && (domainSize is null || number <= domainSize))
                    seen.Add(number);
            }

            // Held counts only once the window has listed the number, so this class, not its callers,
            // guarantees what IsComplete relies on: every held beast is a seen one. Seen fills first.
            foreach (var number in page.Captured)
            {
                if (number > 0 && seen.Contains(number))
                    numbers.Add(number);
            }

            // The largest size seen wins, not the latest: a short one, from a list still loading or an
            // unrecognized view, would revoke an earned claim until the player reopens the window.
            if (page.BeastTotal is > 0 && page.BeastTotal > (beastTotal ?? 0))
                beastTotal = page.BeastTotal;

            // The held count keeps its largest too: pacts are never broken, and a filter only lowers it.
            // It comes only from a page of the remembered size, never a suspect view; filtered views name
            // that size. The -1 floor lets a real 0 count; a null count fails and keeps the last reading.
            if (page.BeastTotal == beastTotal && page.CapturedTotal > (capturedTotal ?? -1))
                capturedTotal = page.CapturedTotal;

            return numbers.Count > heldBefore;
        }
    }

    /// <summary>Takes in a reading of the game's beast record set.</summary>
    /// <param name="reading">An accepted reading, from <see cref="BeastRankRecord.Assess"/>.</param>
    /// <remarks>
    /// A reading is the whole record set at one moment, so it replaces the last one. Numbers past the
    /// end of the bestiary are refused, as in <see cref="RecordPage"/>.
    /// </remarks>
    /// <returns>
    /// True when a rank or a leveled beast differs from the last reading, so the caller acts only on
    /// news.
    /// </returns>
    public bool RecordRanks(BeastRankReading reading)
    {
        lock (gate)
        {
            var nextRanks = new Dictionary<int, int>();

            foreach (var (number, rank) in reading.Ranks)
            {
                if (InDomain(number))
                    nextRanks[number] = rank;
            }

            var nextLeveled = new HashSet<int>();
            foreach (var number in reading.Leveled)
            {
                if (InDomain(number))
                    nextLeveled.Add(number);
            }

            var unchanged = nextRanks.Count == ranks.Count
                && ranks.All(pair => nextRanks.TryGetValue(pair.Key, out var rank) && rank == pair.Value)
                && nextLeveled.SetEquals(leveled);

            ranks = nextRanks;
            leveled = nextLeveled;
            return !unchanged;
        }
    }

    /// <summary>Drops the last reading of the record set.</summary>
    /// <remarks>
    /// Called when a later look at the record set is refused. A reading the bestiary window has since
    /// contradicted, or a record set that fails its checks, cannot be trusted, so its ranks and
    /// leveled beasts stop being reported.
    /// </remarks>
    public void ForgetRanks()
    {
        lock (gate)
        {
            ranks = new();
            leveled = [];
        }
    }

    /// <summary>Each beast's rank from the last reading, by bestiary number.</summary>
    /// <remarks>A copy, so a later reading cannot change what a collection pass is holding.</remarks>
    public IReadOnlyDictionary<int, int> Ranks()
    {
        lock (gate)
            return new Dictionary<int, int>(ranks);
    }

    /// <summary>
    /// Every beast known to be held, ascending: those the window showed as held, plus leveled beasts
    /// the window has never listed.
    /// </summary>
    /// <remarks>
    /// What an upload reports; <see cref="Snapshot"/> is the window's share alone, which the
    /// completeness test and the record-set check rely on. Sorted so the upload log and a pasted
    /// diagnostic read in bestiary order; the server ignores order. Where the sources disagree the
    /// window wins, because a reported pact is never taken back: a beast it listed without marking
    /// held is not reported, whatever the record set says.
    /// </remarks>
    public IReadOnlyList<int> Pacts()
    {
        var pacts = new List<int>();
        lock (gate)
        {
            pacts.AddRange(numbers);

            foreach (var number in leveled)
            {
                if (!seen.Contains(number))
                    pacts.Add(number);
            }
        }

        pacts.Sort();
        return pacts;
    }

    /// <summary>Whether a bestiary number is a real one, as far as the game's data says.</summary>
    /// <remarks>Called with the lock held.</remarks>
    private bool InDomain(int number) => number > 0 && (domainSize is null || number <= domainSize);

    /// <summary>
    /// Tells the ledger how many beasts the game's own data says the bestiary holds.
    /// </summary>
    /// <param name="size">The bestiary's real size, from the game's data sheet.</param>
    /// <remarks>See <see cref="IsComplete"/>.</remarks>
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
    /// Three agreements, which check each other: the window's count of beasts must match the game's
    /// own, the one check that cannot come from the window; every one of those numbers must have been
    /// seen, which a filtered view cannot fake; and the held beasts must match the window's held count,
    /// which catches misread held flags. False until the player has looked at the whole bestiary,
    /// because a fragment must never be presented as the whole.
    /// </remarks>
    public bool IsComplete
    {
        get
        {
            lock (gate)
                return CompleteWhileLocked();
        }
    }

    /// <summary>
    /// The completeness test, for callers already holding the lock, so <see cref="Report"/> can take
    /// its verdict and figures at one moment.
    /// </summary>
    private bool CompleteWhileLocked() =>
        beastTotal is > 0
        && capturedTotal is not null
        && domainSize == beastTotal
        && seen.Count == beastTotal
        && numbers.Count == capturedTotal;

    /// <summary>
    /// Every figure the completeness test rests on, read together, except the bestiary's real size,
    /// which the caller handed in through <see cref="NoteDomainSize"/>.
    /// </summary>
    /// <remarks>
    /// One locked read, so the figures describe one moment. The window's held count appears only here,
    /// so this is how to tell which agreement failed when a claim is withheld. A struct, so asking
    /// allocates nothing on a path the window can walk dozens of times as it opens.
    /// </remarks>
    /// <returns>
    /// The held count, how many numbers have been seen, the window's tally of held and of total,
    /// and whether the whole bestiary has been accounted for.
    /// </returns>
    public LedgerReport Report()
    {
        lock (gate)
            return new LedgerReport(
                numbers.Count, seen.Count, capturedTotal, beastTotal, CompleteWhileLocked());
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
            ranks = new();
            leveled = [];
            capturedTotal = null;
            beastTotal = null;

            // domainSize survives: the sheet's size is true of every character, while the window's
            // must be earned again, or it would vouch for a bestiary nobody has read.
        }
    }

    /// <summary>The beasts the bestiary window has shown as held this session, ascending.</summary>
    public IReadOnlyList<int> Snapshot()
    {
        List<int> snapshot;
        lock (gate)
            snapshot = new List<int>(numbers);

        // Sorted outside the lock: the copy is private, and holding the gate would only delay the
        // window's next page.
        snapshot.Sort();
        return snapshot;
    }
}
