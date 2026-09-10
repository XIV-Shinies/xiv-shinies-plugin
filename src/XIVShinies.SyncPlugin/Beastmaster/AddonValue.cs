namespace XIVShinies.SyncPlugin.Beastmaster;

/// <summary>
/// One of the values a game window was handed to draw itself from, reduced to the shapes that
/// reading it needs.
/// </summary>
/// <remarks>
/// <para>
/// The game's own value type is a union carrying integers, booleans, floats, text and several
/// kinds of pointer, and only three of those matter here. Narrowing to this on the way in is what
/// lets everything downstream be tested without the game: a page of values is then an ordinary
/// list a test can write out by hand.
/// </para>
/// <para>
/// Every property is nullable, and null means "the slot did not hold this kind of thing" rather
/// than "the slot was empty". A reader checking that a slot holds the kind it expects is how
/// misreading a window whose layout has moved is caught.
/// </para>
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
