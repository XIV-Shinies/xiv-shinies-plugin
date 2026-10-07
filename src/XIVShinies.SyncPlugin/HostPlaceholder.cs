using System;

namespace XIVShinies.SyncPlugin;

/// <summary>
/// The placeholder a collection's own copy uses to name the website the plugin uploads to, and the
/// one place it is filled in.
/// </summary>
/// <remarks>
/// <para>
/// Player-facing text names that website by its address (for example <c>xiv-shinies.com</c>),
/// because the backend URL is a setting and a player reads an address more readily than a word
/// like "server". Text the plugin writes itself fills the address in where it is built: the window
/// calls <c>BackendHost()</c>, and the pure copy helpers take it as a parameter. A collection's
/// self-description (<see cref="Collectors.ICollector.WhatGetsSent"/>, its hover details, and the
/// notes it attaches to a read) is written once, before any address is known, so it writes
/// <see cref="Token"/> instead, and the surface that draws it calls <see cref="Fill"/>.
/// </para>
/// <para>
/// Filling at the point of drawing keeps every collection free of settings, which is what lets
/// adding one stay a single class.
/// </para>
/// </remarks>
// A `static class` holds only shared members and is never instantiated: a module of functions.
public static class HostPlaceholder
{
    /// <summary>What a collection's copy writes where the website's address belongs.</summary>
    // `const` fixes the value when the code compiles, so copy can join it with `+` into its own
    // constants.
    public const string Token = "{host}";

    /// <summary>Puts the configured website's address in place of every <see cref="Token"/>.</summary>
    /// <param name="text">Copy that may contain the token.</param>
    /// <param name="host">The configured website's address, as the player reads it.</param>
    // `=>` makes the expression after it the whole method body, like an arrow function.
    // `StringComparison.Ordinal` compares the characters exactly, ignoring the machine's language.
    public static string Fill(string text, string host) =>
        text.Replace(Token, host, StringComparison.Ordinal);
}
