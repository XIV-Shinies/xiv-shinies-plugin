using System.Collections.Generic;
using System.Reflection;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// SourceNoteText turns a source key and scan status into one note for the settings window, which
// draws any non-null note and has no source-name branch of its own.
//
// The tone picks the form. Live and Cached notes are chips: Label is the text and Detail is optional
// hover copy. Missing notes are full lines whose Text names the in-game fix, which must never hide
// behind a hover; the sweep at the bottom of this file pins that.
public class SourceNoteTextTests
{
    // Made up, so a test can tell an address passed in from one written into the copy.
    private const string Host = "shinies.example";

    private static ItemSourceStatus Status(string state, int? count = null, int? total = null) =>
        new() { State = state, Count = count, Total = total };

    // The game fetches a mannequin's contents per interaction and never caches them, so no plugin can
    // read them. Nothing the user does fixes that, so the note is a muted chip with the explanation and
    // workaround in the hover; a full line would nag forever.
    [Fact]
    public void An_unreadable_mannequin_source_is_a_chip_with_the_workaround_in_hover()
    {
        var note = SourceNoteText.Describe(SourceKeys.Mannequins, Status(SourceStates.Unreadable), Host);

        Assert.Equal("Mannequins", note!.Label);
        Assert.Equal(SourceTone.Unreadable, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Gear displayed on housing mannequins can't be read — the game never stores a " +
            "mannequin's contents anywhere the plugin can see. To register a piece: retrieve " +
            "it to your bags, sync, then put it back. " + Host + " keeps proof it saw the piece.",
            note.Detail);
    }

    // The detail names the containers the chip covers, so a user can check by hovering whether their
    // equipped weapon counts.
    [Fact]
    public void A_live_inventory_is_a_chip_naming_the_containers_it_covers()
    {
        var note = SourceNoteText.Describe(SourceKeys.Inventory, Status(SourceStates.Live), Host);

        Assert.Equal("Inventory", note!.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Bags, equipped gear, the armoury chest, and crystals — read directly this pass.",
            note.Detail);
    }

    // Currencies, read from the Currency container and the game's currency subsystem, get their own
    // chip, or the panel would imply they are never read. The game exposes a content-bound currency,
    // such as Occult Crescent's pieces, only inside that content, so the detail discloses that gap.
    [Fact]
    public void Live_currencies_are_a_chip_naming_what_they_cover_and_the_content_bound_gap()
    {
        var note = SourceNoteText.Describe(SourceKeys.Currencies, Status(SourceStates.Live), Host);

        Assert.Equal("Currencies", note!.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Gil, tomestones, scrips, and the game's other currencies — read directly this " +
            "pass. Currencies bound to a specific piece of content, like Occult Crescent's " +
            "pieces, can only be read while inside it.",
            note.Detail);
    }

    // Cached is the saddlebag's healthy resting state, so it is a chip. The detail names the premium
    // saddlebag, which shares this key.
    [Fact]
    public void A_cached_saddlebag_is_a_chip_with_the_refresh_hint_in_its_detail()
    {
        var note = SourceNoteText.Describe(SourceKeys.Saddlebag, Status(SourceStates.Cached), Host);

        Assert.Equal("Saddlebag", note!.Label);
        Assert.Equal(SourceTone.Cached, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Read from cache, including the premium saddlebag — open it once in game to refresh " +
            "as needed.",
            note.Detail);
    }

    // Only the user can open a never-opened source, so the action shows on a full line, never behind
    // a hover.
    [Fact]
    public void An_unscanned_saddlebag_is_a_line_naming_the_action_that_includes_it()
    {
        var note = SourceNoteText.Describe(SourceKeys.Saddlebag, Status(SourceStates.Unscanned), Host);

        Assert.Equal("Saddlebag", note!.Label);
        Assert.Equal(SourceTone.Missing, note.Tone);
        Assert.Equal("Saddlebag: not scanned yet — open it once in game to include it.", note.Text);
    }

    // Missing, not Cached: a never-summoned retainer hides all its contents, like an unopened
    // saddlebag, so the counts are a floor rather than a stale total. The line targets the rest,
    // since summoning them changes the answer.
    [Fact]
    public void Cached_retainers_with_some_missing_are_a_line_with_the_fraction_targeting_the_rest()
    {
        var note = SourceNoteText.Describe(
            SourceKeys.Retainers, Status(SourceStates.Cached, count: 3, total: 5), Host);

        Assert.Equal("Retainers", note!.Label);
        Assert.Equal(SourceTone.Missing, note.Tone);
        Assert.Equal(
            "Retainers: 3/5 read from cache — summon the rest once at a Summoning Bell to include them.",
            note.Text);
    }

    // With every retainer read the source is healthy, so it is a chip. The fraction is information,
    // not an action, so it stays in the label.
    [Fact]
    public void Cached_retainers_all_scanned_are_a_chip_carrying_the_fraction_in_its_label()
    {
        var note = SourceNoteText.Describe(
            SourceKeys.Retainers, Status(SourceStates.Cached, count: 3, total: 3), Host);

        Assert.Equal("Retainers 3/3", note!.Label);
        Assert.Equal(SourceTone.Cached, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Each retainer's bags and equipped gear, read from cache — summon a retainer to " +
            "refresh as needed.",
            note.Detail);
    }

    // The cache keeps every retainer it has ever read, while the total is what the character has now,
    // so a dismissed retainer makes the count outgrow the total. Clamped, so the chip never says "4/3".
    [Fact]
    public void Cached_retainers_never_report_more_read_than_the_character_has()
    {
        var note = SourceNoteText.Describe(
            SourceKeys.Retainers, Status(SourceStates.Cached, count: 4, total: 3), Host);

        Assert.Equal("Retainers 3/3", note!.Label);
        Assert.Equal(SourceTone.Cached, note.Tone);
    }

    // Opening the retainer list is what tells the game how many retainers the character owns, so the
    // detail names it.
    [Fact]
    public void Cached_retainers_without_a_total_carry_the_scanned_count_alone_in_the_label()
    {
        var note = SourceNoteText.Describe(SourceKeys.Retainers, Status(SourceStates.Cached, count: 3), Host);

        Assert.Equal("Retainers 3", note!.Label);
        Assert.Equal(SourceTone.Cached, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Each retainer's bags and equipped gear, read from cache — open the retainer list at " +
            "a Summoning Bell to check for any not yet read.",
            note.Detail);
    }

    [Fact]
    public void Cached_retainers_without_a_count_omit_the_number_from_the_label()
    {
        var note = SourceNoteText.Describe(SourceKeys.Retainers, Status(SourceStates.Cached), Host);

        Assert.Equal("Retainers", note!.Label);
        Assert.Equal(SourceTone.Cached, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal(
            "Each retainer's bags and equipped gear, read from cache — open the retainer list at " +
            "a Summoning Bell to check for any not yet read.",
            note.Detail);
    }

    [Fact]
    public void Unscanned_retainers_are_a_line_naming_the_action_that_includes_them()
    {
        var note = SourceNoteText.Describe(SourceKeys.Retainers, Status(SourceStates.Unscanned), Host);

        Assert.Equal("Retainers", note!.Label);
        Assert.Equal(SourceTone.Missing, note.Tone);
        Assert.Equal(
            "Retainers: not scanned yet — summon each retainer once at a Summoning Bell to include them.",
            note.Text);
    }

    // The armoire's wire state is "loaded", which is current, so it takes the Live tone. The player
    // never sees the word.
    [Fact]
    public void A_loaded_armoire_is_a_healthy_chip()
    {
        var note = SourceNoteText.Describe(SourceKeys.Armoire, Status(SourceStates.Loaded), Host);

        Assert.Equal("Armoire", note!.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
        Assert.Null(note.Text);
        Assert.Null(note.Detail);
    }

    [Fact]
    public void An_unscanned_armoire_is_a_line_naming_the_action_that_includes_it()
    {
        var note = SourceNoteText.Describe(SourceKeys.Armoire, Status(SourceStates.Unscanned), Host);

        Assert.Equal("Armoire", note!.Label);
        Assert.Equal(SourceTone.Missing, note.Tone);
        Assert.Equal("Armoire: not opened yet — open it once in game to include it.", note.Text);
    }

    [Fact]
    public void A_cached_glamour_dresser_is_a_chip_with_the_refresh_hint_in_its_detail()
    {
        var note = SourceNoteText.Describe(SourceKeys.GlamourDresser, Status(SourceStates.Cached), Host);

        Assert.Equal("Glamour Dresser", note!.Label);
        Assert.Equal(SourceTone.Cached, note.Tone);
        Assert.Null(note.Text);
        Assert.Equal("Read from cache — open it once in game to refresh as needed.", note.Detail);
    }

    [Fact]
    public void An_unscanned_glamour_dresser_is_a_line_naming_the_action_that_includes_it()
    {
        var note = SourceNoteText.Describe(SourceKeys.GlamourDresser, Status(SourceStates.Unscanned), Host);

        Assert.Equal("Glamour Dresser", note!.Label);
        Assert.Equal(SourceTone.Missing, note.Tone);
        Assert.Equal("Glamour Dresser: not scanned yet — open it once in game to include it.", note.Text);
    }

    // A source the copy has not caught up with shows nothing rather than a raw wire string.
    [Fact]
    public void An_unknown_source_key_returns_null()
    {
        Assert.Null(SourceNoteText.Describe("facewearCabinet", Status(SourceStates.Live), Host));
    }

    // Silence beats a note for a combination nothing handles.
    [Fact]
    public void A_known_source_in_an_unexpected_state_returns_null()
    {
        Assert.Null(SourceNoteText.Describe(SourceKeys.Inventory, Status(SourceStates.Unscanned), Host));
    }

    [Fact]
    public void A_completely_unknown_state_returns_null()
    {
        Assert.Null(SourceNoteText.Describe(SourceKeys.Saddlebag, Status("teleported"), Host));
    }

    // The window draws a Missing note as a full line, so it must carry Text: a hover may hide optional
    // detail, never a required action. The sweep covers every key, state and count shape, so a new arm
    // in SourceNoteText cannot forget its sentence.
    [Fact]
    public void Every_missing_note_carries_the_full_line_text_its_action_lives_in()
    {
        foreach (var sourceKey in WireConstantsOf(typeof(SourceKeys)))
        {
            foreach (var state in WireConstantsOf(typeof(SourceStates)))
            {
                // Every count shape the collector can produce; only the retainer arms read them.
                foreach (var (count, total) in new (int?, int?)[] { (null, null), (3, 5), (3, 3), (3, null) })
                {
                    var note = SourceNoteText.Describe(sourceKey, Status(state, count, total), Host);

                    if (note is { Tone: SourceTone.Missing })
                        Assert.False(string.IsNullOrEmpty(note.Text));
                }
            }
        }
    }

    // An Unreadable note is a muted chip whose only content beyond its label is the hover Detail, so
    // without one it would be a bare gray chip that explains nothing.
    [Fact]
    public void Every_unreadable_note_carries_the_hover_detail_that_explains_it()
    {
        foreach (var sourceKey in WireConstantsOf(typeof(SourceKeys)))
        {
            foreach (var state in WireConstantsOf(typeof(SourceStates)))
            {
                foreach (var (count, total) in new (int?, int?)[] { (null, null), (3, 5), (3, 3), (3, null) })
                {
                    var note = SourceNoteText.Describe(sourceKey, Status(state, count, total), Host);

                    if (note is { Tone: SourceTone.Unreadable })
                        Assert.False(string.IsNullOrEmpty(note.Detail));
                }
            }
        }
    }

    // By reflection, so the sweeps cover constants added later.
    private static IEnumerable<string> WireConstantsOf(System.Type constantsClass)
    {
        foreach (var field in constantsClass.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.GetRawConstantValue() is string value)
                yield return value;
        }
    }
}
