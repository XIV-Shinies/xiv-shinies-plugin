namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// The warning a sharing card draws when its toggle is ticked but the master switch, a halt, or a
/// character that could not be read stops the sharing.
/// </summary>
/// <remarks>
/// A player who collects nothing may never look past the card, so it says why nothing is shared, and
/// the toggle stays ticked because the choice is still the user's; a reason the server imposes grays
/// the card and chips it "Off" instead. The master switch comes first, since while it is off nothing
/// else runs, and the rest count only while the settings and the server allow the sharing. A halt
/// outranks an unread character, since its fix comes first and a "Sync now" press clears both.
/// </remarks>
public static class SharingCardLine
{
    /// <summary>What the card says while the user's master switch is off.</summary>
    public const string SyncSwitchedOffSentence =
        "Nothing is shared while syncing is switched off. Switch it on at the top of this window.";

    /// <summary>What the card says once the plugin has stopped trying to read the character.</summary>
    public const string CharacterNotReadSentence =
        "Your character could not be read, so nothing is shared. Press Sync now to try again.";

    /// <summary>The line the card draws, or null when it has nothing to explain.</summary>
    /// <param name="stoppedBySyncSwitch">
    /// Whether the master switch is the reason the sharing is not running (the gate's
    /// <c>StoppedBySyncSwitch</c>).
    /// </param>
    /// <param name="canRun">
    /// Whether the settings and the server let the sharing run (the gate's <c>CanTrack</c> or
    /// <c>CanShare</c>).
    /// </param>
    /// <param name="halted">Whether syncing has stopped until the user fixes something.</param>
    /// <param name="characterNotRead">Whether the plugin has stopped trying to read the character.</param>
    /// <param name="haltSentence">The sentence naming the halt's fix (see <see cref="HaltCopy"/>).</param>
    public static string? For(
        bool stoppedBySyncSwitch, bool canRun, bool halted, bool characterNotRead, string haltSentence)
    {
        if (stoppedBySyncSwitch)
            return SyncSwitchedOffSentence;

        // Another gate is shut: the toggle, the server (its "Off" chip), onboarding, the token, or a
        // config that has not arrived. None is a reason this line names.
        if (!canRun)
            return null;

        if (halted)
            return $"Sharing has stopped. {haltSentence}";

        return characterNotRead ? CharacterNotReadSentence : null;
    }
}
