using System;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// Validation for the backend server URL. Dalamud recommends letting users point a plugin at
/// their own server rather than forcing the maintainer's, so this value is user-overridable —
/// but an override must never silently downgrade the connection to plaintext.
/// </summary>
/// <remarks>
/// The rules: the host must always be a <b>DNS name, never a raw IP address</b> (Dalamud requires
/// this, with no exemption — use <c>localhost</c> rather than <c>127.0.0.1</c>); and
/// <c>https://</c> is required for any remote host, with <c>http://</c> tolerated only for
/// loopback so local development works. Note the token travels in an <c>Authorization</c> header
/// on every request, so it is sent to whatever host is configured here — which is exactly why
/// plaintext to a remote host is refused outright.
/// </remarks>
public static class BackendUrl
{
    /// <summary>The official server, addressed by DNS hostname (never a raw IP).</summary>
    public const string Default = "https://xiv-shinies.com";

    // Scheme + host + port of the official server, computed once for comparison.
    private static readonly string DefaultAuthority =
        new Uri(Default).GetLeftPart(UriPartial.Authority);

    // The official server's host and profile page, for the fallbacks in DisplayHost and ProfileUrl.
    private static readonly string DefaultHost = new Uri(Default).Host;

    private static readonly string DefaultProfileUrl =
        new Uri(new Uri(Default), "/profile").ToString();

    /// <summary>
    /// True when the given URL points at the official server. Used to decide whether the user
    /// must first acknowledge that their token will be sent to a server we do not run.
    /// </summary>
    public static bool IsDefault(Uri uri) =>
        string.Equals(
            uri.GetLeftPart(UriPartial.Authority), DefaultAuthority, StringComparison.OrdinalIgnoreCase);

    /// <summary>The host to name in any sentence that has to identify the configured server.</summary>
    /// <remarks>
    /// <para>
    /// The base URL is user-overridable, so a sentence naming the service has to name the one the
    /// plugin is actually talking to. A privacy card naming a host nothing is sent to is untrue, an
    /// instruction naming the wrong site sends the user somewhere that cannot help them, and an
    /// outage notice naming the wrong server sends a developer hunting a failure elsewhere.
    /// </para>
    /// <para>
    /// The host alone, since this lands mid-sentence where a scheme and a trailing slash read as
    /// debris, and in its <c>IdnHost</c> form — punycode for an international host, because
    /// characters from other scripts can spell a domain indistinguishable from another at a glance.
    /// Judged by <see cref="TryNormalize"/>, the gate <see cref="ProfileUrl"/> also uses, so the two
    /// can never name different servers; a value it rejects is one the client refuses to contact.
    /// </para>
    /// </remarks>
    /// <param name="baseUrl">The configured base URL, which may be absent or unparseable.</param>
    public static string DisplayHost(string? baseUrl) =>
        TryNormalize(baseUrl, out var uri, out _) && uri is not null ? uri.IdnHost : DefaultHost;


    /// <summary>Why the configured server cannot be contacted, as a sentence for the user.</summary>
    /// <remarks>
    /// <para>
    /// Two different settings produce the same refusal, and they need opposite actions: an address
    /// that breaks the scheme or hostname rules has to be rewritten, while a perfectly good address
    /// the user has not confirmed they meant is waiting on one flag. Telling the second user to fix
    /// their address would send them looking for a fault that is not there.
    /// </para>
    /// <para>
    /// A malformed address borrows <see cref="TryNormalize"/>'s own complaint rather than restating
    /// it, so the sentence names the rule that was actually broken.
    /// </para>
    /// </remarks>
    /// <param name="baseUrl">The configured base URL.</param>
    /// <param name="customBackendAcknowledged">
    /// Whether the user has confirmed they meant to send their token to a server this project does
    /// not run. Nothing leaves until they have.
    /// </param>
    public static string DescribeUnusableSetting(string? baseUrl, bool customBackendAcknowledged)
    {
        if (!TryNormalize(baseUrl, out var uri, out var error))
            return $"The server address in the plugin's configuration file cannot be used. {error}";

        if (uri is not null && !IsDefault(uri) && !customBackendAcknowledged)
        {
            return "The plugin is set to a server this project does not run, which has not been "
                + "acknowledged. Nothing is sent until it is.";
        }

        // Neither setting explains it, so say only what is certain rather than guessing at a cause.
        return "The plugin is not configured to contact a server.";
    }

    /// <summary>Where to send a user who needs to create or manage a plugin token.</summary>
    /// <remarks>
    /// <para>
    /// On the configured server, because a token is only valid on the server that issued it.
    /// Opening the official site while the plugin talks to another would hand the user a
    /// credential their backend rejects, with nothing on screen explaining why.
    /// </para>
    /// <para>
    /// Built through <see cref="TryNormalize"/> rather than by joining strings, because this value
    /// is handed to the operating system to open. Normalizing here means only a URL that already
    /// satisfies the scheme and hostname rules can be opened, and anything else falls back to the
    /// official site rather than launching whatever the config file happened to contain.
    /// </para>
    /// </remarks>
    /// <param name="baseUrl">The configured base URL, which may be absent or unparseable.</param>
    public static string ProfileUrl(string? baseUrl) =>
        TryNormalize(baseUrl, out var uri, out _) && uri is not null
            ? new Uri(uri, "/profile").ToString()
            : DefaultProfileUrl;


    /// <summary>
    /// Validates and normalizes a user-entered URL.
    /// </summary>
    /// <param name="raw">The raw text the user typed; may be null, blank, or padded with spaces.</param>
    /// <param name="normalized">The parsed URI when valid, otherwise null.</param>
    /// <param name="error">A user-facing explanation when invalid, otherwise null.</param>
    /// <returns>True when the URL is usable.</returns>
    // The `out` keyword means the method *returns a value through* that parameter — the caller
    // passes a variable that this method fills in. C# has no tuple destructuring in the JS sense,
    // so `bool TryX(input, out result, out error)` is the idiomatic "parse that can fail" shape.
    public static bool TryNormalize(string? raw, out Uri? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Enter a server URL.";
            return false;
        }

        // UriKind.Absolute rejects relative values like "xiv-shinies.com" (no scheme).
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            error = "That is not a valid absolute URL.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            error = "The URL must start with https:// (or http:// for a local server).";
            return false;
        }

        // Dalamud requires plugins to reach a backend by DNS hostname rather than a raw IP
        // address, and states no exemption — so this rejects loopback IPs too. Nothing is lost:
        // "localhost" is a DNS name and reaches the same place as 127.0.0.1.
        //
        // Two checks, because Uri only classifies DOTTED addresses as IPv4: the obscure single-
        // number encodings of an IPv4 address (decimal "2130706433", hex "0x7f000001" — both
        // 127.0.0.1) parse as DNS names by Uri's rules, yet the OS resolver still treats them as
        // addresses. IPAddress.TryParse recognizes those spellings, closing the loophole.
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            || System.Net.IPAddress.TryParse(uri.Host, out _))
        {
            error = "Enter the server by domain name (use localhost, not 127.0.0.1).";
            return false;
        }

        // A URL may carry "user:password@" before the host, and nothing here wants it. The plugin
        // authenticates with its own token, so credentials in the URL buy nothing — while they
        // cost two real things. They ride along into the address the request is built from, and
        // into the profile link handed to the operating system, where the password would land in
        // the browser's address bar and history. They also make one host impersonate another:
        // "https://xiv-shinies.com@evil.example" reads as the official site at a glance, and it
        // is the userinfo, not the host, that the eye lands on first.
        if (uri.UserInfo.Length > 0)
        {
            error = "The URL must not contain a username or password.";
            return false;
        }

        // Uri.IsLoopback is true for the host "localhost" (and for loopback IPs, already rejected
        // above). Note it is NOT true for lookalikes such as "localhost.evil.com".
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            error = "Only https:// is allowed for a remote server; your token would otherwise " +
                    "be sent unencrypted.";
            return false;
        }

        normalized = uri;
        return true;
    }
}
