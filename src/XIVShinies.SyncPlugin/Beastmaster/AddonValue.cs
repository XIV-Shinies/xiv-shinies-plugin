namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>One value a game window draws from, narrowed to the shapes the readers use.</summary>
/// <remarks>
/// The game's value type is a union of integers, booleans, floats, text and several pointer kinds.
/// Narrowing it to the three that matter here lets readers work on a plain list with no game types.
/// Null means the slot did not hold that kind, not that it was empty. A reader checking that each slot
/// holds the kind it expects is how a window whose layout moved gets caught.
/// </remarks>
public readonly record struct AddonValue(uint? Number, bool? Flag, string? Text)
{
    /// <summary>A slot holding a whole number.</summary>
    public static AddonValue FromInteger(uint value) => new(value, null, null);

    /// <summary>A slot holding a yes-or-no flag.</summary>
    public static AddonValue FromBoolean(bool value) => new(null, value, null);

    /// <summary>A slot holding text.</summary>
    public static AddonValue FromText(string value) => new(null, null, value);

    /// <summary>A slot holding something none of the readers here can use.</summary>
    public static AddonValue Unreadable => new(null, null, null);
}
