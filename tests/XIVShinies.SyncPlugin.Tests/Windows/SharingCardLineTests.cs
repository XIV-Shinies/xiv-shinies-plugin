using Xunit;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

// The warning a sharing card draws when its toggle is ticked but nothing is shared, and which
// reason wins when several hold.
public class SharingCardLineTests
{
    private const string HaltSentence = "Fix it on the website.";

    private static string? LineFor(bool switchOff, bool canRun, bool halted, bool characterNotRead) =>
        SharingCardLine.For(
            stoppedBySyncSwitch: switchOff,
            canRun: canRun,
            halted: halted,
            characterNotRead: characterNotRead,
            haltSentence: HaltSentence);

    // While the master switch is off nothing else runs, so it names itself whatever else holds.
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void The_master_switch_outranks_every_other_reason(bool canRun, bool halted, bool characterNotRead)
    {
        Assert.Equal(
            SharingCardLine.SyncSwitchedOffSentence, LineFor(true, canRun, halted, characterNotRead));
    }

    // canRun is false while another gate is shut: the toggle, the server, onboarding, the token, or a
    // config not yet arrived. The line names none of those.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_sharing_that_could_not_run_anyway_has_no_line(bool halted, bool characterNotRead)
    {
        Assert.Null(LineFor(false, canRun: false, halted, characterNotRead));
    }

    // A halt outranks a character that could not be read: one press clears both.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_halt_names_its_fix(bool characterNotRead)
    {
        Assert.Equal(
            $"Sharing has stopped. {HaltSentence}",
            LineFor(false, canRun: true, halted: true, characterNotRead));
    }

    [Fact]
    public void A_character_that_could_not_be_read_asks_for_a_press()
    {
        Assert.Equal(
            SharingCardLine.CharacterNotReadSentence,
            LineFor(false, canRun: true, halted: false, characterNotRead: true));
    }

    [Fact]
    public void A_running_sharing_has_no_line()
    {
        Assert.Null(LineFor(false, canRun: true, halted: false, characterNotRead: false));
    }

    // The exact sentences, so a reworded line is a deliberate change.
    [Fact]
    public void The_sentences_are_pinned()
    {
        Assert.Equal(
            "Nothing is shared while syncing is switched off. Switch it on at the top of this window.",
            SharingCardLine.SyncSwitchedOffSentence);
        Assert.Equal(
            "Your character could not be read, so nothing is shared. Press Sync now to try again.",
            SharingCardLine.CharacterNotReadSentence);
    }
}
