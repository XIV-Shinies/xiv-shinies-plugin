using System;
using System.Threading;
using System.Threading.Tasks;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// Runs the <c>GET /me</c> probe for the onboarding wizard, off the framework thread.
/// </summary>
/// <remarks>
/// Awaiting HTTP in <c>Draw()</c> would stall the game, so the probe runs as a background task and a
/// later frame picks up the parked answer. What the answer means is
/// <see cref="Onboarding.TokenCheck"/>'s to decide.
/// </remarks>
internal sealed class TokenVerifier : IDisposable
{
    private readonly ApiClient apiClient;

    /// <summary>Canceled when the window is torn down, so a probe in flight stops.</summary>
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>Captured before the source can be disposed; reading `.Token` afterwards throws.</summary>
    private readonly CancellationToken lifetimeToken;

    /// <summary>
    /// The probe's answer, or null while none has arrived. Written by the background task, read by
    /// the draw thread.
    /// </summary>
    private volatile ApiResponse<MeResponse>? result;

    /// <summary>True while a probe is running. Shared across threads like <see cref="result"/>.</summary>
    private volatile bool inFlight;

    /// <summary>
    /// Bumped whenever the question changes, so an answer to an old question can be recognized and
    /// thrown away.
    /// </summary>
    /// <remarks>
    /// A probe cannot be recalled, so without this an answer for an edited-away token would show
    /// "Token accepted" beside one never checked.
    /// </remarks>
    private volatile int generation;

    public TokenVerifier(ApiClient apiClient)
    {
        this.apiClient = apiClient;
        lifetimeToken = lifetime.Token;
    }

    /// <summary>True while a probe is in flight.</summary>
    public bool InFlight => inFlight;

    /// <summary>Starts a probe. Does nothing if one is already running.</summary>
    public void Start()
    {
        if (inFlight)
            return;

        result = null;
        inFlight = true;

        // Captured now, so the task compares against the question it was started for.
        var startedFor = ++generation;

        // No token for Task.Run: a canceled one would skip the delegate, and with it the `finally`
        // that clears inFlight, leaving the button on "Checking..." for good.
        _ = Task.Run(() => ProbeAsync(startedFor));
    }

    /// <summary>
    /// Takes the probe's answer if one has arrived, clearing it so it is consumed exactly once.
    /// </summary>
    /// <returns>The answer, or null when none is waiting.</returns>
    public ApiResponse<MeResponse>? TakeResult()
    {
        var answer = result;
        if (answer is not null)
            result = null;

        return answer;
    }

    /// <summary>
    /// Discards the answer to the previous question, as when the user edits the token.
    /// </summary>
    /// <remarks>
    /// The generation bump also discards a probe still in flight; clearing <see cref="result"/> alone
    /// would not.
    /// </remarks>
    public void Forget()
    {
        generation++;
        result = null;
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }

    /// <param name="startedFor">The generation this probe was started for.</param>
    private async Task ProbeAsync(int startedFor)
    {
        try
        {
            var answer = await apiClient.GetMeAsync(lifetimeToken).ConfigureAwait(false);

            // After an edit, publishing this would vouch for a token never checked.
            if (startedFor == generation)
                result = answer;
        }
        catch (OperationCanceledException)
        {
            // The window closed mid-probe. Nothing to report.
        }
        catch (Exception)
        {
            // ApiClient reports failures as statuses, so this is unexpected. Reported as Unknown, which
            // TokenCheck shows as an unreachable server, rather than lost in a discarded task.
            if (startedFor == generation)
                result = new ApiResponse<MeResponse> { Status = ApiStatus.Unknown };
        }
        finally
        {
            // Even for a superseded probe: a flag left set would block every later probe.
            inFlight = false;
        }
    }
}
