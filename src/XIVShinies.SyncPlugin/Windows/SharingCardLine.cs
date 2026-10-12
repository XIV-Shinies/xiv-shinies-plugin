namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// The warning a sharing card (the live tracker's, or the Crucible run sharing's) draws beneath its
/// disclosure when the user's toggle is ticked but the sharing is not running for one of the reasons
/// it names: the master switch, a halt, or a character that could not be read.
/// </summary>
/// <remarks>
/// <para>
/// A player who only shares, with every collection switched off, may never look past the sharing
/// card, so the card itself says why nothing is shared. Each reason keeps the toggle ticked, because
/// the choice is still the user's: a reason the server imposes draws the card grayed and chipped
/// "Off" instead, and needs no line.
/// </para>
/// <para>
/// One line at most, in a fixed order. The master switch comes first: while it is off nothing else
/// runs, so no other reason applies. The rest only count while the settings and the server would
/// otherwise let the sharing run. A halt then outranks a character that could not be read, because
/// its sentence names the fix the player must make first, and a "Sync now" press clears both.
/// </para>
/// </remarks>
// A `static class` holds only shared members and is never instantiated: a module of functions.
public static class SharingCardLine
{
    /// <summary>What the card says while the user's master switch is off.</summary>
    // A `const` is fixed when the code compiles and belongs to the class rather than to an instance,
    // so callers read it as `SharingCardLine.SyncSwitchedOffSentence`, like an exported constant.
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
    // `string?` means the result may be null, like `string | null` in TypeScript.
    public static string? For(
        bool stoppedBySyncSwitch, bool canRun, bool halted, bool characterNotRead, string haltSentence)
    {
        if (stoppedBySyncSwitch)
            return SyncSwitchedOffSentence;

        // Another gate is shut: the toggle, the server (its "Off" chip), onboarding, the token, or a
        // config that has not arrived. None is a reason this line names.
        if (!canRun)
            return null;

        // `$"...{x}..."` is an interpolated string, like a TypeScript template literal.
        if (halted)
            return $"Sharing has stopped. {haltSentence}";

        // `a ? b : c` picks b when a is true, else c, as in TypeScript.
        return characterNotRead ? CharacterNotReadSentence : null;
    }
}
