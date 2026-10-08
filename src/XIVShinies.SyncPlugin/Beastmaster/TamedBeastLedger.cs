using System.Collections.Generic;
// LINQ: query methods such as `All` on any collection, like JavaScript's array methods.
using System.Linq;

namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>What a ledger holds at one moment, and whether that adds up to the whole bestiary.</summary>
/// <param name="Held">How many beasts the bestiary window has shown as held.</param>
/// <param name="Seen">How many bestiary numbers have been listed to it, held or not.</param>
/// <param name="CapturedTotal">The window's own count of held beasts, or null when unread.</param>
/// <param name="BeastTotal">The window's own count of beasts that exist, or null when unread.</param>
/// <param name="IsComplete">Whether the whole bestiary has been accounted for.</param>
// A `readonly record struct` is a small immutable value: the compiler writes the constructor,
// value-based equality and a readable ToString, and `struct` means the value is copied when it is
// passed around rather than referenced, so a local like the one Report hands back needs no heap
// allocation. The nearest JavaScript analogue is a frozen plain object, except that copying is free
// and each copy is independent.
public readonly record struct LedgerReport(
    int Held,
    int Seen,
    int? CapturedTotal,
    int? BeastTotal,
    bool IsComplete);

/// <summary>
/// The bestiary numbers the character is known to hold this login session, how much of their
/// bestiary has been read, and each beast's rank once it has been read.
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
/// character's collection to another. Nothing is lost by that: paging through the bestiary, which
/// marks every beast held, rebuilds the held set, as talking to the Crucible's NPC does the ranks.
/// </para>
/// <para>
/// Every access is locked. The writers — the bestiary window's callback, the frame-tick read of the
/// record set, and the clears — run on the game's main thread alongside the collection pass that
/// reads, so the lock is uncontended and costs nothing. It is there because that single thread is an
/// assumption about how the game dispatches, not something this class can enforce: should any caller
/// ever arrive from elsewhere, a set updated in steps can be left half-updated, and a read racing that
/// update can hand back a set that never existed.
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

    // Each beast's rank, by bestiary number, from the last reading of the game's beast record set.
    // A separate source from the window's pages, so it is kept apart from the held numbers, which
    // the completeness test counts and must therefore come from the window alone.
    private Dictionary<int, int> ranks = new();

    // The beasts that reading shows as raised past a fresh pact, and so held (see
    // BeastRankReading.Leveled).
    private HashSet<int> leveled = [];

    // The object every read and write below locks on. A private, dedicated instance rather than the
    // set itself or `this`, so no outside code can take the same lock and deadlock against it.
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

            // Bounded by the game's own bestiary size when it is known, so a number past the end of
            // the sheet is refused rather than left to make the count add up by accident.
            foreach (var number in page.Seen)
            {
                if (number > 0 && (domainSize is null || number <= domainSize))
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

            // The largest size ever seen wins, rather than the latest. A size that comes back short
            // — from a list still loading, or from a view this code does not recognize — is measured
            // against the sheet by the completeness test, so letting it shrink the remembered one
            // would revoke a claim the player has already earned and can only win back by opening
            // the window again.
            if (page.BeastTotal is > 0 && page.BeastTotal > (beastTotal ?? 0))
                beastTotal = page.BeastTotal;

            // The held count is kept the same way, and it is the half a filter actually moves: a
            // filtered view of a fifty-beast bestiary still calls it fifty while counting only the
            // held beasts the filter shows. Pacts are never broken, so within a session the largest
            // count the window has shown is the true one.
            //
            // Only from a page whose size agrees with the remembered one, so a count is never taken
            // from a view the size check has already found suspect. Filtered views pass that
            // freely, since they name the real size.
            //
            // The floor is -1 rather than 0 so that a genuine "none held" can be recorded: a player
            // holding no beasts reads 0 out of 50, and 0 must count as an answer rather than as no
            // answer. A null count fails the comparison outright, which is how a page whose tally
            // would not parse leaves an earlier good reading alone.
            if (page.BeastTotal == beastTotal && page.CapturedTotal > (capturedTotal ?? -1))
                capturedTotal = page.CapturedTotal;

            return numbers.Count > heldBefore;
        }
    }

    /// <summary>Takes in a reading of the game's beast record set.</summary>
    /// <param name="reading">An accepted reading, from <see cref="BeastRankRecord.Assess"/>.</param>
    /// <remarks>
    /// A reading is the whole record set at one moment, so it replaces the last one rather than
    /// adding to it. Numbers past the end of the bestiary are refused, as <see cref="RecordPage"/>
    /// refuses them.
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

            // `var (number, rank)` unpacks each map entry into its key and value, like
            // `for (const [number, rank] of map)` in JavaScript.
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

            // Two readings match only with the same beasts at the same ranks and the same leveled
            // set. `All(pair => ...)` asks whether every entry passes, like `every` on an array; the
            // lookup inside hands back the other reading's rank through `out`. `SetEquals` compares
            // two sets regardless of order.
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
    /// contradicted, or a record set that no longer passes its checks, says nothing that can be
    /// trusted, so its ranks and leveled beasts stop being reported.
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
    /// Every beast known to be held, ascending: those the window showed as held, and those the record
    /// set shows as leveled that the window has not listed as not held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What an upload reports. <see cref="Snapshot"/> is the window's share alone, which the
    /// completeness test and the check against a reading of the record set rely on. Sorted for the
    /// reader rather than for the server, which does not care about order: the upload log and a
    /// pasted diagnostic are easier to scan in bestiary order.
    /// </para>
    /// <para>
    /// Where the two sources disagree the window wins: a beast it listed without marking held is not
    /// reported, whatever the record set says, because a pact reported is never taken back. A beast
    /// the window has not listed at all is the record set's to vouch for.
    /// </para>
    /// </remarks>
    public IReadOnlyList<int> Pacts()
    {
        var pacts = new List<int>();
        lock (gate)
        {
            pacts.AddRange(numbers);

            // Only a beast the window has never listed is added from here.
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
    /// <remarks>
    /// The one figure in this class that does not come from the window. A window naming its own
    /// slice as the whole bestiary cannot be answered from inside itself; see
    /// <see cref="IsComplete"/> for what this agrees with and what the others catch.
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
    /// First, the window's idea of how many beasts exist must match the game's own — otherwise
    /// every figure here would come from the window, and a window describing part of the bestiary
    /// as the whole of it would agree with itself perfectly. This is the check that cannot come
    /// from the window.
    /// </para>
    /// <para>
    /// Second, every one of those numbers must actually have been seen, which is what a filtered
    /// view cannot fake: it can shrink the held count beside the tally, but it cannot list numbers
    /// it is not showing. Third, the beasts recorded as held must match the count the window
    /// reports, which catches a page whose held flags were misread even when the right number of
    /// records was found.
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
                return CompleteWhileLocked();
        }
    }

    /// <summary>The completeness test itself, for callers that already hold the lock.</summary>
    /// <remarks>
    /// The test as one expression, so that both <see cref="IsComplete"/> and <see cref="Report"/>
    /// can run it while already holding the gate — <see cref="Report"/> needs its verdict and the
    /// figures behind it to describe one moment.
    /// </remarks>
    private bool CompleteWhileLocked() =>
        beastTotal is > 0
        && capturedTotal is not null
        && domainSize == beastTotal
        && seen.Count == beastTotal
        && numbers.Count == capturedTotal;

    /// <summary>
    /// Every figure the completeness test rests on except the bestiary's real size, read together;
    /// that one is the caller's own, handed in through <see cref="NoteDomainSize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One locked read rather than several, so the numbers cannot describe different moments. The
    /// window's own count of held beasts is here and nowhere else on this class, so without this a
    /// withheld claim gives no way to tell which agreement failed.
    /// </para>
    /// <para>
    /// A struct, so asking costs no allocation on a path the bestiary window can walk dozens of
    /// times as it opens.
    /// </para>
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

            // domainSize survives while beastTotal does not. They are the same quantity, so what
            // separates them is where each came from: the sheet is read from the game's data and is
            // true of every character, while the window's figure is a claim that has to be earned
            // again by looking at the next character's bestiary. Keeping the earned one would let
            // it vouch for a bestiary nobody has read.
        }
    }

    /// <summary>The beasts the bestiary window has shown as held this session, ascending.</summary>
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
