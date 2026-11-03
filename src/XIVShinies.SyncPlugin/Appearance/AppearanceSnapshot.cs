using System;
using System.Text.Json.Nodes;

namespace XIVShinies.SyncPlugin.Appearance;

/// <summary>The four display toggles that change how the character's gear is drawn.</summary>
/// <param name="WeaponHidden">True when the character's weapon is hidden.</param>
/// <param name="HatHidden">True when the character's headgear is hidden.</param>
/// <param name="VisorToggled">True when the headgear's visor is toggled.</param>
/// <param name="VieraEarsHidden">True when a Viera character's ears are hidden.</param>
/// <remarks>
/// Four booleans in a row are easy to swap, so build one with named arguments. The
/// <c>default</c> value, all four false, is an ordinary reading, not an unset one.
/// </remarks>
public readonly record struct AppearanceToggles(
    bool WeaponHidden,
    bool HatHidden,
    bool VisorToggled,
    bool VieraEarsHidden);

/// <summary>
/// Assembles the <c>appearance</c> category's facts: the local character's look, as raw values
/// copied out of the game.
/// </summary>
/// <remarks>
/// Reports the bytes as the game stores them, without unpacking packed bytes, turning palette indices
/// into colors or judging plausibility; XIV Shinies does that when it draws the character (see
/// <see cref="CustomizeFields"/>). Built as a <see cref="JsonObject"/> because
/// <see cref="Api.ApiJson.Options"/> camel-cases dictionary keys, which would send <c>"race"</c>
/// where the contract says <c>"Race"</c>.
/// </remarks>
public static class AppearanceSnapshot
{
    /// <summary>
    /// The layout version, sent as the facts' <c>version</c>. It tells the server which keys exist and
    /// what each value means.
    /// </summary>
    public const int CurrentVersion = 1;

    // The crest bitfield's three slot bits; the other five are unused.
    private const byte CrestOffHand = 0x01;
    private const byte CrestHead = 0x02;
    private const byte CrestBody = 0x04;

    // The game stores two glasses slots. The second is unused in practice, but both are sent so the
    // shape holds if the game fills it.
    private const int GlassesSlots = 2;

    /// <summary>
    /// Builds the category's facts, or returns null when the customization read is the wrong size.
    /// </summary>
    /// <param name="customize">
    /// The customization bytes, in <see cref="CustomizeFields.Names"/> order. Any other length means
    /// an unknown layout or a read from the wrong place, so no <c>appearance</c> key is sent (see
    /// <see cref="Collectors.CollectSkipReasons.UnexpectedLayout"/>).
    /// </param>
    /// <param name="toggles">The four display toggles.</param>
    /// <param name="crestBits">
    /// The free company crest bitfield: <c>0x01</c> off-hand, <c>0x02</c> head, <c>0x04</c> body.
    /// </param>
    /// <param name="glasses">
    /// The <c>Glasses</c> sheet row id in each slot, always sent as two entries: a missing one is 0
    /// and extras are dropped. Padding is safe because the second slot is 0 in practice, while a
    /// customize read of the wrong length means the byte-to-name mapping is unknown.
    /// </param>
    public static JsonObject? Build(
        ReadOnlySpan<byte> customize,
        AppearanceToggles toggles,
        byte crestBits,
        ReadOnlySpan<ushort> glasses)
    {
        if (customize.Length != CustomizeFields.Names.Count)
            return null;

        // JsonObject keeps insertion order, so the wire lists the bytes in the game's order.
        var customizeNode = new JsonObject();
        for (var i = 0; i < customize.Length; i++)
        {
            // A byte-backed JsonValue refuses GetValue<int>() until serialized. The wire output is
            // the same JSON number either way.
            customizeNode[CustomizeFields.Names[i]] = (int)customize[i];
        }

        var glassesNode = new JsonArray();
        for (var slot = 0; slot < GlassesSlots; slot++)
        {
            // An int, so GetValue<int>() reads it, as in the customize loop.
            glassesNode.Add(slot < glasses.Length ? (int)glasses[slot] : 0);
        }

        return new JsonObject
        {
            ["version"] = CurrentVersion,
            ["customize"] = customizeNode,
            ["glasses"] = glassesNode,
            ["fcCrest"] = new JsonObject
            {
                ["head"] = (crestBits & CrestHead) != 0,
                ["body"] = (crestBits & CrestBody) != 0,
                ["offHand"] = (crestBits & CrestOffHand) != 0,
            },
            ["weaponHidden"] = toggles.WeaponHidden,
            ["hatHidden"] = toggles.HatHidden,
            ["visorToggled"] = toggles.VisorToggled,
            ["vieraEarsHidden"] = toggles.VieraEarsHidden,
        };
    }
}
