using System;

namespace XIVShinies.SyncPlugin;

/// <summary>
/// The placeholder a collection's own copy uses to name the website the plugin uploads to, and the
/// one place it is filled in.
/// </summary>
/// <remarks>
/// Player-facing text names the website by its address, such as <c>xiv-shinies.com</c>, since the
/// backend URL is a setting and a player reads an address more readily than "server". A collection's
/// self-description (<see cref="Collectors.ICollector.WhatGetsSent"/>, its hover details, and its read
/// notes) is written before any address is known, so it writes <see cref="Token"/> and the surface
/// that draws it calls <see cref="Fill"/>. That keeps collections free of settings, so adding one
/// stays a single class.
/// </remarks>
public static class HostPlaceholder
{
    /// <summary>What a collection's copy writes where the website's address belongs.</summary>
    // A `const`, so copy can join it with `+` into its own constants.
    public const string Token = "{host}";

    /// <summary>Puts the configured website's address in place of every <see cref="Token"/>.</summary>
    /// <param name="text">Copy that may contain the token.</param>
    /// <param name="host">The configured website's address, as the player reads it.</param>
    public static string Fill(string text, string host) =>
        text.Replace(Token, host, StringComparison.Ordinal);
}
