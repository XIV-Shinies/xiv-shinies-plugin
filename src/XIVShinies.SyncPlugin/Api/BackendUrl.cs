using System;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// Validation for the backend server URL, which is user-overridable per Dalamud's guidance. An
/// override must never downgrade the connection to plaintext.
/// </summary>
/// <remarks>
/// The host must be a DNS name, never a raw IP: Dalamud requires it with no exemption, so use
/// <c>localhost</c>, not <c>127.0.0.1</c>. A remote host needs <c>https://</c>; <c>http://</c> is
/// allowed only for loopback, because the token rides in every request's <c>Authorization</c> header.
/// </remarks>
public static class BackendUrl
{
    /// <summary>The official server, addressed by DNS hostname.</summary>
    public const string Default = "https://xiv-shinies.com";

    private static readonly string DefaultAuthority =
        new Uri(Default).GetLeftPart(UriPartial.Authority);

    // The fallbacks for DisplayHost and ProfileUrl.
    private static readonly string DefaultHost = new Uri(Default).Host;

    private static readonly string DefaultProfileUrl =
        new Uri(new Uri(Default), "/profile").ToString();

    /// <summary>
    /// True when the URL points at the official server. Any other server needs the user to
    /// acknowledge that it receives their token.
    /// </summary>
    public static bool IsDefault(Uri uri) =>
        string.Equals(
            uri.GetLeftPart(UriPartial.Authority), DefaultAuthority, StringComparison.OrdinalIgnoreCase);

    /// <summary>The host to name in any sentence that has to identify the configured server.</summary>
    /// <remarks>
    /// The base URL is user-overridable, so the sentence must name the server the plugin actually
    /// talks to. It gives the bare host in punycode (<c>IdnHost</c>): bare because it lands
    /// mid-sentence, and punycode because characters from other scripts can spell a lookalike domain.
    /// <see cref="TryNormalize"/> judges it, as it does <see cref="ProfileUrl"/>, so the two never name
    /// different servers.
    /// </remarks>
    /// <param name="baseUrl">The configured base URL, which may be absent or unparseable.</param>
    public static string DisplayHost(string? baseUrl) =>
        TryNormalize(baseUrl, out var uri, out _) && uri is not null ? uri.IdnHost : DefaultHost;


    /// <summary>Why the configured server cannot be contacted, as a sentence for the user.</summary>
    /// <remarks>
    /// A malformed address has to be rewritten, while a valid one the user has not acknowledged waits
    /// only on that flag, so the two get different sentences. A malformed address quotes
    /// <see cref="TryNormalize"/>'s complaint, naming the rule it broke.
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
    /// On the configured server, since a token is valid only on the server that issued it. Built
    /// through <see cref="TryNormalize"/> because the operating system opens this value: an address
    /// that fails the scheme or hostname rules falls back to the official site.
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
    public static bool TryNormalize(string? raw, out Uri? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Enter a server URL.";
            return false;
        }

        // Absolute rejects a value with no scheme, such as "xiv-shinies.com".
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

        // Uri classifies only dotted IPv4 as an address. IPAddress.TryParse also catches single-number
        // spellings such as "2130706433" or "0x7f000001", which the OS still resolves to 127.0.0.1.
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            || System.Net.IPAddress.TryParse(uri.Host, out _))
        {
            error = "Enter the server by domain name (use localhost, not 127.0.0.1).";
            return false;
        }

        // Userinfo buys nothing, since the token authenticates. It would ride into every request and
        // the browser history, and "https://xiv-shinies.com@evil.example" passes for the official site.
        if (uri.UserInfo.Length > 0)
        {
            error = "The URL must not contain a username or password.";
            return false;
        }

        // IsLoopback is true for "localhost" but not for lookalikes such as "localhost.evil.com".
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
