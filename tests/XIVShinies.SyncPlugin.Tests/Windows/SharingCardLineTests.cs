using Xunit;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

// The warning a sharing card draws when its toggle is ticked but nothing is shared, and which
// reason wins when several hold. Each `[Fact]` is one test, like `it(...)` in Jest; a `[Theory]`
// runs once per `[InlineData]` row, like `it.each`.
public class SharingCardLineTests
{
    // `const` fixes the value when the code compiles, like a module-level `const` in TypeScript;
    // `private` keeps it inside this class.
    private const string HaltSentence = "Fix it on the website.";

    // `=>` makes the expression after it the whole method body, like an arrow function without
    // braces. `string?` means the result may be null, like `string | null`. The named arguments say
    // which flag each value is.
    private static string? LineFor(bool switchOff, bool canRun, bool halted, bool characterNotRead) =>
        SharingCardLine.For(
            stoppedBySyncSwitch: switchOff,
            canRun: canRun,
            halted: halted,
            characterNotRead: characterNotRead,
            haltSentence: HaltSentence);

    // The master switch names itself whatever else holds: while it is off, nothing else runs. Every
    // combination of the other three is pinned.
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

    // With another gate shut (the toggle, the server, onboarding, the token, or a config that has not
    // arrived), none is a reason the line names, so no line is added.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_sharing_that_could_not_run_anyway_has_no_line(bool halted, bool characterNotRead)
    {
        Assert.Null(LineFor(false, canRun: false, halted, characterNotRead));
    }

    // A halt names its fix, and outranks a character that could not be read: one press clears both.
    // `$"...{x}..."` is an interpolated string, like a TypeScript template literal.
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
