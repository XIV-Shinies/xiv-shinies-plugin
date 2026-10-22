using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The HTTPS client for the XIV Shinies plugin API. It owns one <see cref="HttpClient"/> for the
/// plugin's lifetime.
/// </summary>
/// <remarks>
/// Await every method off the framework thread; blocking on one there deadlocks the game. Every
/// failure, unload included, comes back as an <see cref="ApiStatus"/>; only the caller's own
/// cancellation throws <see cref="OperationCanceledException"/>. Nothing here fires on its own,
/// which keeps the "no silent first-run upload" rule.
/// </remarks>
public sealed class ApiClient : IDisposable
{
    private const string ApiPrefix = "api/plugin/v1";

    // Real responses are kilobytes. The cap stops a hostile or broken backend (the URL is
    // user-overridable) from streaming a body that exhausts the game's memory.
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    // Retry-After is server-controlled and backoff survives logout, so an unbounded value could
    // silence syncing for the session. A day matches the full-sync interval ceiling.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);

    // One client for every request; one per call exhausts sockets.
    private readonly HttpClient http;

    // Canceled at unload, so a request in flight ends cleanly instead of failing against a
    // disposed HttpClient.
    private readonly CancellationTokenSource lifetime = new();

    // Copied before the source can be disposed (see SyncManager's field of the same name).
    private readonly CancellationToken lifetimeToken;

    private readonly PluginSettings settings;

    // Covers the body read too. HttpClient.Timeout stops counting once the headers arrive
    // (SendAsync uses ResponseHeadersRead), so a stalled body would otherwise stay in flight.
    private readonly TimeSpan requestTimeout;

    /// <summary>Creates a client bound to the given settings.</summary>
    /// <param name="settings">Live settings; the token and base URL are re-read per request.</param>
    /// <param name="pluginVersion">Version string for the <c>User-Agent</c> header.</param>
    /// <param name="messageHandler">A transport override; null uses a real network handler.</param>
    /// <param name="requestTimeout">
    /// How long one request may take, body included; 30 seconds when omitted.
    /// </param>
    public ApiClient(
        PluginSettings settings,
        string pluginVersion,
        HttpMessageHandler? messageHandler = null,
        TimeSpan? requestTimeout = null)
    {
        this.settings = settings;
        lifetimeToken = lifetime.Token;

        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);

        if (messageHandler is null)
        {
            // No redirects: a 3xx would hand the choice of host back to the server, past
            // BackendUrl's checks. .NET also strips the Authorization header on a redirect.
            var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            http = new HttpClient(handler, disposeHandler: true);
        }
        else
        {
            // The caller owns this handler.
            http = new HttpClient(messageHandler, disposeHandler: false);
        }

        http.Timeout = this.requestTimeout;

        // The contract requires a User-Agent. TryParseAdd, so a malformed version sends none
        // rather than throwing out of the constructor and aborting the load.
        http.DefaultRequestHeaders.UserAgent.TryParseAdd(PluginMeta.UserAgent(pluginVersion));
    }

    /// <summary>Calls <c>GET /me</c>: the token's account and its characters.</summary>
    public Task<ApiResponse<MeResponse>> GetMeAsync(CancellationToken cancellationToken = default) =>
        SendAsync<MeResponse>(HttpMethod.Get, "me", content: null, cancellationToken);

    /// <summary>Fetches <c>GET /config</c>: kill switches, cadence, and the item manifest.</summary>
    public Task<ApiResponse<ConfigResponse>> GetConfigAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ConfigResponse>(HttpMethod.Get, "config", content: null, cancellationToken);

    /// <summary>Uploads a collection snapshot via <c>POST /sync</c>.</summary>
    public Task<ApiResponse<SyncResponse>> PostSyncAsync(
        SyncRequest request, CancellationToken cancellationToken = default) =>
        PostJsonAsync<SyncRequest, SyncResponse>("sync", request, cancellationToken);

    /// <summary>
    /// Uploads a live occult instance snapshot via <c>POST /occult/instance-state</c>.
    /// </summary>
    public Task<ApiResponse<OccultInstanceStateResponse>> PostOccultInstanceStateAsync(
        OccultInstanceStateRequest request, CancellationToken cancellationToken = default) =>
        PostJsonAsync<OccultInstanceStateRequest, OccultInstanceStateResponse>(
            "occult/instance-state", request, cancellationToken);

    /// <summary>
    /// Uploads Crucible run snapshots via <c>POST /crucible/observations</c>.
    /// </summary>
    public Task<ApiResponse<CrucibleObservationsResponse>> PostCrucibleObservationsAsync(
        CrucibleObservationsRequest request, CancellationToken cancellationToken = default) =>
        PostJsonAsync<CrucibleObservationsRequest, CrucibleObservationsResponse>(
            "crucible/observations", request, cancellationToken);

    /// <summary>Serializes a request body and POSTs it.</summary>
    // Generic rather than object, so the serializer works from the compile-time type.
    private async Task<ApiResponse<TResponse>> PostJsonAsync<TRequest, TResponse>(
        string path, TRequest request, CancellationToken cancellationToken)
        where TResponse : class
    {
        StringContent content;

        // Serializing runs outside SendAsync's try, so it needs its own guard to keep an exception
        // out of the game.
        try
        {
            var json = JsonSerializer.Serialize(request, ApiJson.Options);

            // StringContent sets Content-Length, which the contract requires: a chunked body
            // without it is rejected with 413.
            content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new ApiResponse<TResponse> { Status = ApiStatus.InvalidPayload };
        }

        return await SendAsync<TResponse>(HttpMethod.Post, path, content, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels any request still in flight, then releases the underlying <see cref="HttpClient"/>.
    /// </summary>
    // Cancel first: disposing the handler under a live request throws ObjectDisposedException in
    // the awaiting continuation.
    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
        http.Dispose();
    }

    // The one code path every request funnels through.
    private async Task<ApiResponse<T>> SendAsync<T>(
        HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
        where T : class
    {
        // These guards run before the request owns the content, so each one disposes it.

        // A malformed token can only earn an opaque 401.
        if (!settings.HasUsableToken())
        {
            content?.Dispose();
            return new ApiResponse<T> { Status = ApiStatus.NotConfigured };
        }

        // Never send the token over plaintext to a remote host, or to a raw IP address.
        if (!BackendUrl.TryNormalize(settings.BaseUrl, out var baseUri, out _))
        {
            content?.Dispose();
            return new ApiResponse<T> { Status = ApiStatus.NotConfigured };
        }

        // A non-default backend receives the token, so it needs the user's acknowledgment.
        // Checking here too means a settings UI bug cannot ship the token to another server.
        if (!BackendUrl.IsDefault(baseUri!) && !settings.CustomBackendAcknowledged)
        {
            content?.Dispose();
            return new ApiResponse<T> { Status = ApiStatus.NotConfigured };
        }

        // Set once the headers arrive, so a failure during the body read can still report it.
        int? arrivedCode = null;

        try
        {
            // Built from the authority only, so a path in the user's base URL cannot change the
            // endpoint: "https://host/foo" still reaches "https://host/api/plugin/v1/me".
            var url = $"{baseUri!.GetLeftPart(UriPartial.Authority)}/{ApiPrefix}/{path}";

            // Canceled by the caller, by unload, or by the deadline below.
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, cancellationToken);

            // One deadline covers the body read too (see requestTimeout). It ends in an
            // OperationCanceledException, which the first catch reports as a network error.
            linked.CancelAfter(requestTimeout);

            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
            request.Content = content;

            // ResponseHeadersRead returns before the body is buffered, so an oversized
            // Content-Length is refused before any of the body is read.
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            var rawCode = (int)response.StatusCode;
            arrivedCode = rawCode;

            // Decides only success or failure; a failure's final status needs its error body.
            var status = ApiStatusMap.FromHttpStatusCode(rawCode);

            // A huge body would take down the game, not just the plugin (see MaxResponseBytes).
            // ReadBodyAsync catches a server that lies about the length or omits it.
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                return new ApiResponse<T> { Status = ApiStatus.NetworkError, HttpStatusCode = rawCode };

            var body = await ReadBodyAsync(response.Content, linked.Token).ConfigureAwait(false);
            if (body is null)
                return new ApiResponse<T> { Status = ApiStatus.NetworkError, HttpStatusCode = rawCode };

            if (status == ApiStatus.Ok)
            {
                var value = Deserialize<T>(body);

                // A 200 with an empty or unparseable body is not a success.
                return value is null
                    ? new ApiResponse<T> { Status = ApiStatus.Unknown, HttpStatusCode = rawCode }
                    : new ApiResponse<T> { Status = ApiStatus.Ok, Value = value, HttpStatusCode = rawCode };
            }

            // A 403's `error` code says which of the four character refusals it is. A body that
            // does not parse leaves the code null, which a 403 reads as the not-claimed case.
            var error = Deserialize<ErrorResponse>(body);

            return new ApiResponse<T>
            {
                Status = ApiStatusMap.FromHttpStatusCode(rawCode, error?.Error),
                Error = error,
                RetryAfter = ReadRetryAfter(response),
                HttpStatusCode = rawCode,
            };
        }
        // Timeouts and canceled body reads may throw a plain OperationCanceledException. Unless the
        // caller canceled, which propagates, the cause was the deadline or unload: a network error.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ApiResponse<T> { Status = ApiStatus.NetworkError, HttpStatusCode = arrivedCode };
        }
        catch (HttpRequestException)
        {
            // DNS failure, refused connection, or TLS problem: no status arrived.
            return new ApiResponse<T> { Status = ApiStatus.NetworkError, HttpStatusCode = arrivedCode };
        }
        catch (IOException)
        {
            // The connection dropped mid-body. That read throws IOException (HttpIOException
            // derives from it), not HttpRequestException.
            return new ApiResponse<T> { Status = ApiStatus.NetworkError, HttpStatusCode = arrivedCode };
        }
        catch (ObjectDisposedException)
        {
            // The plugin unloaded while this request was starting or in flight.
            return new ApiResponse<T> { Status = ApiStatus.NetworkError, HttpStatusCode = arrivedCode };
        }
    }

    /// <summary>
    /// Reads a response body as a string, or returns null once it passes
    /// <see cref="MaxResponseBytes"/>, whatever its <c>Content-Length</c> claimed.
    /// </summary>
    /// <remarks>
    /// The buffer is sized from the advertised length, so only a server that omits the header costs
    /// a cap-sized allocation. Bytes past the limit are never stored.
    /// </remarks>
    private static async Task<string?> ReadBodyAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);

        // The advertised length is server-controlled, so it is clamped here too and this helper
        // stays safe on its own. No header falls back to the cap.
        var limit = (int)Math.Min(content.Headers.ContentLength ?? MaxResponseBytes, MaxResponseBytes);

        // One spare byte: a body of exactly the limit reads whole, and the first byte past it trips
        // the check.
        var buffer = new byte[limit + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(total), token)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
        }

        if (total > limit)
            return null;

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>
    /// Reads how long the server asked us to wait, from its <c>Retry-After</c> header.
    /// </summary>
    /// <remarks>
    /// Reads both legal forms: our server sends seconds, but a proxy or CDN may rewrite them to an
    /// HTTP date. A past date means retry now. Clamped to <see cref="MaxRetryAfter"/>.
    /// </remarks>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
            return null;

        if (header.Delta is { } delta)
            return Clamp(delta);

        if (header.Date is { } date)
            return Clamp(date - DateTimeOffset.UtcNow);

        return null;

        static TimeSpan Clamp(TimeSpan wait) =>
            wait < TimeSpan.Zero ? TimeSpan.Zero
            : wait > MaxRetryAfter ? MaxRetryAfter
            : wait;
    }

    // A malformed body, or a type the serializer cannot handle, returns null rather than throwing
    // into the game.
    private static T? Deserialize<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(body, ApiJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
