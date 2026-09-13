namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>The figures one read of the Master's Bestiary produces, as the observer logs them.</summary>
/// <param name="Listed">How many beasts the page listed.</param>
/// <param name="Held">How many of those the page showed as held.</param>
/// <param name="PageCapturedTotal">The page's own count of held beasts, or null when unread.</param>
/// <param name="PageBeastTotal">The page's own count of beasts that exist, or null when unread.</param>
/// <param name="Ledger">What the ledger held once the page was recorded.</param>
/// <param name="SheetSize">The bestiary's size from the game's data, or null when unread.</param>
/// <remarks>
/// <para>
/// What the observer compares against the last read it logged, to decide whether a redraw is worth
/// a log line; see <see cref="TamedBeastObserver"/>'s bestiary handler for when it writes one.
/// </para>
/// <para>
/// A <c>readonly record struct</c>, so two summaries compare equal exactly when every field matches,
/// including each field of the nested <see cref="LedgerReport"/>, which is a record struct too.
/// Comparing two of them allocates nothing.
/// </para>
/// </remarks>
public readonly record struct BestiaryReadSummary(
    int Listed,
    int Held,
    int? PageCapturedTotal,
    int? PageBeastTotal,
    LedgerReport Ledger,
    int? SheetSize);
