using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Onboarding;

/// <summary>What the wizard currently knows about the token the user pasted.</summary>
public enum TokenCheckState
{
    /// <summary>No probe has been made, or the token changed since the last one.</summary>
    NotChecked,

    /// <summary>A probe is in flight.</summary>
    Checking,

    /// <summary>The server recognized the token.</summary>
    Valid,

    /// <summary>The token is wrong. Only the user can fix this.</summary>
    Invalid,

    /// <summary>We could not ask. The token may be perfectly good.</summary>
    Unreachable,
}

/// <summary>
/// Interprets a <c>GET /me</c> probe for the onboarding wizard.
/// </summary>
/// <remarks>
/// It separates <b>"your token is wrong"</b> from <b>"we could not ask"</b>. Merging them would blame
/// the user for an outage, or leave them retrying a token that will never work.
/// </remarks>
public static class TokenCheck
{
    /// <summary>Maps the outcome of the probe onto what the wizard should say about it.</summary>
    public static TokenCheckState FromApiStatus(ApiStatus status) => status switch
    {
        ApiStatus.Ok => TokenCheckState.Valid,

        // A 401 never heals on retry: unknown, revoked, or malformed.
        ApiStatus.InvalidToken => TokenCheckState.Invalid,

        // Never sent: the token failed its shape check, or the backend is one the client refuses.
        // "Could not reach the server" would be false, and the fix is the user's either way.
        ApiStatus.NotConfigured => TokenCheckState.Invalid,

        // A timeout, a 500, the kill switch, or an unknown status says nothing about the token.
        _ => TokenCheckState.Unreachable,
    };
}
