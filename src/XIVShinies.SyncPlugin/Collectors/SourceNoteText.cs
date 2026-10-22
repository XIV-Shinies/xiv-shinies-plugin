using System;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Turns a per-source scan status into one <see cref="SourceNote"/> for the settings window, or null
/// when there is nothing worth saying.
/// </summary>
/// <remarks>
/// The window renders the note as given and never branches on its source, so a new source gets a note
/// by reporting a status this copy already covers. A source contributing nothing yet gets a full line
/// ending in the in-game action that folds it in, since the plugin cannot open a container itself. An
/// unknown source or state returns null: a raw wire string like <c>"unscanned"</c> means nothing to a
/// player.
/// </remarks>
public static class SourceNoteText
{
    /// <summary>Describes one source's scan status, or returns null when there is nothing to say.</summary>
    /// <param name="sourceKey">The <see cref="SourceKeys"/> wire key naming the storage source.</param>
    /// <param name="status">That source's scan status from the most recent item pass.</param>
    /// <param name="host">The configured website's address, named where a note says who keeps proof.</param>
    public static SourceNote? Describe(string sourceKey, ItemSourceStatus status, string host) =>
        (sourceKey, status.State) switch
        {
            // The detail names which containers travel under the one "inventory" key, so a user can
            // hover to learn whether their equipped weapon counts.
            (SourceKeys.Inventory, SourceStates.Live) =>
                new SourceNote
                {
                    Label = "Inventory",
                    Tone = SourceTone.Live,
                    Detail = "Bags, equipped gear, the armoury chest, and crystals — read directly " +
                        "this pass.",
                },

            // Its own source, though read in the inventory's pass: some currencies live outside any
            // container. The game exposes a content-bound currency only inside that content.
            (SourceKeys.Currencies, SourceStates.Live) =>
                new SourceNote
                {
                    Label = "Currencies",
                    Tone = SourceTone.Live,
                    Detail = "Gil, tomestones, scrips, and the game's other currencies — read " +
                        "directly this pass. Currencies bound to a specific piece of content, " +
                        "like Occult Crescent's pieces, can only be read while inside it.",
                },

            // Cached is the saddlebag's healthy state: the game never offers a live read of it. The
            // detail names the premium half, which travels under the same key.
            (SourceKeys.Saddlebag, SourceStates.Cached) =>
                new SourceNote
                {
                    Label = "Saddlebag",
                    Tone = SourceTone.Cached,
                    Detail = "Read from cache, including the premium saddlebag — open it once in " +
                        "game to refresh as needed.",
                },

            (SourceKeys.Saddlebag, SourceStates.Unscanned) =>
                new SourceNote
                {
                    Label = "Saddlebag",
                    Tone = SourceTone.Missing,
                    Text = "Saddlebag: not scanned yet — open it once in game to include it.",
                },

            // Whether this is healthy or a gap depends on the counts, not the state alone.
            (SourceKeys.Retainers, SourceStates.Cached) =>
                DescribeCachedRetainers(status.Count, status.Total),

            (SourceKeys.Retainers, SourceStates.Unscanned) =>
                new SourceNote
                {
                    Label = "Retainers",
                    Tone = SourceTone.Missing,
                    Text = "Retainers: not scanned yet — summon each retainer once at a Summoning " +
                        "Bell to include them.",
                },

            // "Loaded" is the armoire's live state: the game fetches it on the first open each
            // session, and it stays current after that. The player sees a plain healthy chip.
            (SourceKeys.Armoire, SourceStates.Loaded) =>
                new SourceNote { Label = "Armoire", Tone = SourceTone.Live },

            (SourceKeys.Armoire, SourceStates.Unscanned) =>
                new SourceNote
                {
                    Label = "Armoire",
                    Tone = SourceTone.Missing,
                    Text = "Armoire: not opened yet — open it once in game to include it.",
                },

            // Cached is the glamour dresser's healthy resting state.
            (SourceKeys.GlamourDresser, SourceStates.Cached) =>
                new SourceNote
                {
                    Label = "Glamour Dresser",
                    Tone = SourceTone.Cached,
                    Detail = "Read from cache — open it once in game to refresh as needed.",
                },

            (SourceKeys.GlamourDresser, SourceStates.Unscanned) =>
                new SourceNote
                {
                    Label = "Glamour Dresser",
                    Tone = SourceTone.Missing,
                    Text = "Glamour Dresser: not scanned yet — open it once in game to include it.",
                },

            // Never readable (see SourceKeys.Mannequins), so a muted chip, not a line that nags forever.
            // "Keeps proof": a sighting proves a piece for good, a count does not.
            (SourceKeys.Mannequins, SourceStates.Unreadable) =>
                new SourceNote
                {
                    Label = "Mannequins",
                    Tone = SourceTone.Unreadable,
                    Detail = "Gear displayed on housing mannequins can't be read — the game " +
                        "never stores a mannequin's contents anywhere the plugin can see. To " +
                        "register a piece: retrieve it to your bags, sync, then put it back. " +
                        $"{host} keeps proof it saw the piece.",
                },

            _ => null,
        };

    // A never-summoned retainer hides all its contents, so a partial cache is Missing: the counts are
    // only a floor, and the summon action must stay visible. With no count the label is bare, not "0".
    private static SourceNote DescribeCachedRetainers(int? count, int? total) =>
        (count, total) switch
        {
            ({ } scanned, { } known) when scanned < known =>
                new SourceNote
                {
                    Label = "Retainers",
                    Tone = SourceTone.Missing,
                    Text = $"Retainers: {scanned}/{known} read from cache — summon the rest once " +
                        "at a Summoning Bell to include them.",
                },

            // Clamped: the cache remembers every retainer it ever read, while the total is what the
            // character has now, so a dismissed retainer would show "4/3".
            ({ } scanned, { } known) =>
                new SourceNote
                {
                    Label = $"Retainers {Math.Min(scanned, known)}/{known}",
                    Tone = SourceTone.Cached,
                    Detail = "Each retainer's bags and equipped gear, read from cache — summon a " +
                        "retainer to refresh as needed.",
                },

            // The total is known only once the retainer list is fetched at a Summoning Bell. Until
            // then these two arms stay Cached rather than nag on a guess, and name that cheap check.
            ({ } scanned, null) =>
                new SourceNote
                {
                    Label = $"Retainers {scanned}",
                    Tone = SourceTone.Cached,
                    Detail = "Each retainer's bags and equipped gear, read from cache — open the " +
                        "retainer list at a Summoning Bell to check for any not yet read.",
                },

            _ => new SourceNote
            {
                Label = "Retainers",
                Tone = SourceTone.Cached,
                Detail = "Each retainer's bags and equipped gear, read from cache — open the " +
                    "retainer list at a Summoning Bell to check for any not yet read.",
            },
        };
}

/// <summary>
/// How trustworthy a source note is, so the settings window can render it without knowing which
/// source or scan state produced it.
/// </summary>
/// <remarks>
/// The window maps each tone to a color, an icon and a form: <see cref="Missing"/> draws as a full
/// line, every other tone as a chip. It switches on the tone, never a source name or wire state, so a
/// new source's notes render without touching the drawing code.
/// </remarks>
public enum SourceTone
{
    /// <summary>
    /// Read directly this pass, or, for the armoire, loaded and current: the healthy state.
    /// </summary>
    Live,

    /// <summary>
    /// Real data from a local cache, possibly stale since the source was last opened. The healthy
    /// resting state for containers that work that way.
    /// </summary>
    Cached,

    /// <summary>A gap the user can close: the source, or part of it, has contributed nothing yet.</summary>
    Missing,

    /// <summary>
    /// The game never exposes this source to plugins, and no player action changes that. A muted chip,
    /// never an alarm, with any optional workaround in its hover.
    /// </summary>
    Unreadable,
}

/// <summary>
/// One source note for the settings window's "Reading from:" panel, in either of its forms: a compact
/// chip, or a full line stating what this pass did not read.
/// </summary>
/// <remarks>
/// The window prints what it is handed, with no branch on source or scan state; the tone picks the
/// form. Hover may hide optional information, never what the user needs to know: <see cref="Detail"/>
/// is hover-only, and the always-visible <see cref="Text"/> carries a gap and what resolves it. A test
/// sweeps the copy set so every Missing note has <see cref="Text"/>; one without it draws as a chip.
/// </remarks>
public sealed record SourceNote
{
    /// <summary>The source's short display name, the chip's whole visible text.</summary>
    public required string Label { get; init; }

    /// <summary>How trustworthy this note is; also picks its rendered form.</summary>
    public required SourceTone Tone { get; init; }

    /// <summary>
    /// The full line's sentence, written for a player and never a raw wire string. Present on every
    /// Missing note.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Optional hover copy for the chip: what the source covers, or an optional refresh action.
    /// Required on <see cref="SourceTone.Unreadable"/> notes, where it is the chip's only content
    /// beyond its label (a test sweeps the copy set to enforce it).
    /// </summary>
    public string? Detail { get; init; }
}
