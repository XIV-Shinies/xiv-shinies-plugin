using System;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// Client-side shape check for the XIV Shinies API token. Only the server can say whether a token is
/// valid; the check lets a mistyped or truncated paste fail at once instead of earning an opaque 401.
/// </summary>
public static class TokenFormat
{
    /// <summary>
    /// The <c>xvs_</c> prefix every token starts with, defined by the server, not the plugin. It lets
    /// secret scanners spot a leaked token, and both sides reject junk before any lookup.
    /// </summary>
    public const string Prefix = "xvs_";

    // 32 random bytes encoded as unpadded base64url is always exactly 43 characters.
    private const int BodyLength = 43;

    /// <summary>
    /// True when <paramref name="token"/> has the exact shape the server issues:
    /// <c>xvs_</c> followed by 43 base64url characters.
    /// </summary>
    public static bool IsWellFormed(string? token)
    {
        // A whitespace-only string fails the prefix check below, so it needs no special case.
        if (string.IsNullOrEmpty(token))
            return false;

        if (!token.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        if (token.Length != Prefix.Length + BodyLength)
            return false;

        for (var i = Prefix.Length; i < token.Length; i++)
        {
            if (!IsBase64UrlCharacter(token[i]))
                return false;
        }

        return true;
    }

    // base64url swaps standard base64's '+' and '/' for '-' and '_', and drops '=' padding.
    private static bool IsBase64UrlCharacter(char c) =>
        c is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9'
        or '-'
        or '_';
}
