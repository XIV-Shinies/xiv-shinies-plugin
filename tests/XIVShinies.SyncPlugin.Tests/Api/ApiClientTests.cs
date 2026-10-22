using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using XIVShinies.SyncPlugin;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;
using XIVShinies.SyncPlugin.Occult;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Api;

// A fake HttpMessageHandler replaces only the socket, so the client's real header, URL and status
// handling runs.
public class ApiClientTests
{
    private const string ValidToken = "xvs_0123456789012345678901234567890123456789abc";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;

        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public long? LastContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
                LastContentLength = request.Content.Headers.ContentLength;
            }

            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (ApiClient Client, StubHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string token = ValidToken,
        string baseUrl = "https://xiv-shinies.com",
        bool customBackendAcknowledged = false,
        TimeSpan? requestTimeout = null)
    {
        var handler = new StubHandler(respond);
        var settings = new PluginSettings
        {
            Token = token,
            BaseUrl = baseUrl,
            CustomBackendAcknowledged = customBackendAcknowledged,
        };
        return (new ApiClient(settings, "1.2.3", handler, requestTimeout), handler);
    }

    [Fact]
    public async Task GetMe_sends_bearer_auth_and_user_agent_to_the_contract_url()
    {
        const string body = """
        {"characters": [], "user": {"id": "abc"}}
        """;
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, body));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.Ok, response.Status);
        Assert.Equal("abc", response.Value!.User.Id);

        var request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://xiv-shinies.com/api/plugin/v1/me", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(ValidToken, request.Headers.Authorization.Parameter);
        Assert.Equal("XIVShinies.SyncPlugin/1.2.3", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task GetConfig_parses_the_remote_config()
    {
        const string body = """
        {"categories": {"achievements": true, "items": true, "minions": true,
                        "mounts": true, "quests": true},
         "enabled": false,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [7851], "manifestVersion": "abc123"}
        """;
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, body));

        var response = await client.GetConfigAsync();

        Assert.Equal(ApiStatus.Ok, response.Status);
        Assert.False(response.Value!.Enabled);
        Assert.Equal("https://xiv-shinies.com/api/plugin/v1/config", handler.LastRequest!.RequestUri!.ToString());
    }

    // The contract requires a Content-Length on every upload; the server rejects a chunked body with
    // 413.
    [Fact]
    public async Task PostSync_sends_json_with_a_content_length()
    {
        const string body = """
        {"ok": true, "bound": false,
         "written": {"achievements": 0, "minions": 0, "mounts": 0, "quests": 1}}
        """;
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, body));

        var request = new SyncRequest
        {
            CharacterContentIdHash = new string('a', 64),
            CharacterName = "Some Name",
            HomeWorld = "Excalibur",
            PluginVersion = "1.2.3",
            Trigger = SyncTrigger.Manual,
            Collections = new Dictionary<string, JsonNode>
            {
                ["quests"] = SyncFacts.Ids(new uint[] { 65575 }),
            },
        };

        var response = await client.PostSyncAsync(request);

        Assert.Equal(ApiStatus.Ok, response.Status);
        Assert.Equal(1, response.Value!.Written["quests"]);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://xiv-shinies.com/api/plugin/v1/sync", handler.LastRequest.RequestUri!.ToString());
        Assert.NotNull(handler.LastContentLength);
        Assert.Contains("\"trigger\":\"manual\"", handler.LastBody);
        Assert.Contains("\"quests\":[65575]", handler.LastBody);
        // Monotonic writes: an unread category never appears.
        Assert.DoesNotContain("achievements", handler.LastBody);
    }

    // The occult upload shares the guarded POST path with /sync.
    [Fact]
    public async Task PostOccultInstanceState_posts_to_the_contract_url()
    {
        const string body = """
        {"ok": true, "outcome": "applied",
         "trackerId": "6f9619ff-8b86-d011-b42d-00cf4fc964ff", "created": true}
        """;
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, body));

        var request = OccultUploadBuilder.Build(
            characterContentIdHash: new string('a', 64),
            characterName: "Some Name",
            homeWorld: "Excalibur",
            pluginVersion: "1.2.3",
            territoryTypeId: 1252,
            trigger: OccultTrigger.Enter,
            encounters: [],
            currentWorldId: null);

        var response = await client.PostOccultInstanceStateAsync(request);

        Assert.Equal(ApiStatus.Ok, response.Status);
        Assert.Equal(OccultOutcomes.Applied, response.Value!.Outcome);
        Assert.Equal("6f9619ff-8b86-d011-b42d-00cf4fc964ff", response.Value.TrackerId);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "https://xiv-shinies.com/api/plugin/v1/occult/instance-state",
            handler.LastRequest.RequestUri!.ToString());
        Assert.NotNull(handler.LastContentLength);
        Assert.Contains("\"trigger\":\"enter\"", handler.LastBody);
    }

    [Fact]
    public async Task PostCrucibleObservations_posts_to_the_contract_url()
    {
        const string body = """
        {"ok": true, "outcome": "applied", "runId": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
         "events": 3, "skipped": 0}
        """;
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, body));

        var request = CrucibleUploadBuilder.Request(
            new string('a', 64), "Some Name", "Excalibur", "1.2.3",
            CrucibleTrigger.Enter, territoryTypeId: 1339, []);

        var response = await client.PostCrucibleObservationsAsync(request);

        Assert.Equal(ApiStatus.Ok, response.Status);
        Assert.Equal(CrucibleOutcomes.Applied, response.Value!.Outcome);
        Assert.Equal("6f9619ff-8b86-d011-b42d-00cf4fc964ff", response.Value.RunId);
        Assert.Equal(3, response.Value.Events);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "https://xiv-shinies.com/api/plugin/v1/crucible/observations",
            handler.LastRequest.RequestUri!.ToString());

        Assert.NotNull(handler.LastContentLength);
        Assert.Contains("\"trigger\":\"enter\"", handler.LastBody);
    }

    [Fact]
    public async Task A_malformed_token_short_circuits_without_touching_the_network()
    {
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, "{}"), token: "not-a-token");

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NotConfigured, response.Status);
        Assert.True(ApiStatusMap.IsTerminal(response.Status));
        Assert.Equal(0, handler.CallCount); // never sent — the whole point of the local check
    }

    [Fact]
    public async Task A_plaintext_remote_base_url_short_circuits_without_leaking_the_token()
    {
        var (client, handler) = Build(_ => Json(HttpStatusCode.OK, "{}"), baseUrl: "http://evil.example");

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NotConfigured, response.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task A_401_surfaces_the_opaque_invalid_token_error()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.Unauthorized, """{"error": "invalid_token"}"""));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.InvalidToken, response.Status);
        Assert.Equal("invalid_token", response.Error!.Error);
        Assert.True(ApiStatusMap.IsTerminal(response.Status));
        Assert.Null(response.Value);
    }

    [Fact]
    public async Task A_403_carries_the_echoed_character_name_and_world()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.Forbidden,
            """{"error": "character_not_claimed", "name": "Some Name", "world": "Excalibur"}"""));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.CharacterNotClaimed, response.Status);
        Assert.Equal("Some Name", response.Error!.Name);
        Assert.Equal("Excalibur", response.Error.World);
    }

    // The client reads a 403's body before deciding what it means, so each refusal reaches the sync
    // card and the upload log as its own status.
    [Theory]
    [InlineData("character_not_verified", ApiStatus.CharacterNotVerified)]
    [InlineData("character_ambiguous", ApiStatus.CharacterAmbiguous)]
    [InlineData("character_bound_elsewhere", ApiStatus.CharacterBoundElsewhere)]
    [InlineData("character_something_new", ApiStatus.CharacterNotClaimed)]
    public async Task A_403_reports_which_character_refusal_the_server_named(
        string errorCode, ApiStatus expected)
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.Forbidden,
            $$"""{"error": "{{errorCode}}", "name": "Some Name", "world": "Excalibur"}"""));

        var response = await client.GetMeAsync();

        Assert.Equal(expected, response.Status);
        Assert.Equal(errorCode, response.Error!.Error);
    }

    // A 403 is a failed character match even when its body is not the contract's JSON.
    [Fact]
    public async Task A_403_without_a_readable_body_reads_as_not_claimed()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.Forbidden, "Forbidden"));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.CharacterNotClaimed, response.Status);
    }

    // Two bodies with no code: an explicit null, and no `error` field at all, which fails the DTO's
    // required member so no error body parses.
    [Theory]
    [InlineData("""{"error": null, "name": "Some Name", "world": "Excalibur"}""")]
    [InlineData("""{"name": "Some Name", "world": "Excalibur"}""")]
    public async Task A_403_whose_body_names_no_code_reads_as_not_claimed(string body)
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.Forbidden, body));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.CharacterNotClaimed, response.Status);
    }

    // The tests above stub /me, which never sends a 403. Every call shares one send path; this pins
    // it on /sync, the upload the sync card reports on.
    [Fact]
    public async Task PostSync_reports_the_character_refusal_the_server_named()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.Forbidden,
            """{"error": "character_not_verified", "name": "Some Name", "world": "Excalibur"}"""));

        var request = new SyncRequest
        {
            CharacterContentIdHash = new string('a', 64),
            CharacterName = "Some Name",
            HomeWorld = "Excalibur",
            PluginVersion = "1.2.3",
            Trigger = SyncTrigger.Manual,
            Collections = new Dictionary<string, JsonNode>
            {
                ["quests"] = SyncFacts.Ids(new uint[] { 65575 }),
            },
        };

        var response = await client.PostSyncAsync(request);

        Assert.Equal(ApiStatus.CharacterNotVerified, response.Status);
        Assert.True(RetryPolicy.RequiresUserAction(response.Status));
    }

    [Fact]
    public async Task A_429_reports_the_retry_after_delay()
    {
        var (client, _) = Build(_ =>
        {
            var response = Json((HttpStatusCode)429, """{"error": "rate_limited"}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.RateLimited, response.Status);
        Assert.True(ApiStatusMap.ShouldBackOff(response.Status));
        Assert.Equal(TimeSpan.FromSeconds(30), response.RetryAfter);
    }

    [Fact]
    public async Task A_503_means_the_global_kill_switch_is_off()
    {
        var (client, _) = Build(_ =>
        {
            var response = Json(HttpStatusCode.ServiceUnavailable, """{"error": "sync_disabled"}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3600));
            return response;
        });

        var response = await client.GetConfigAsync();

        Assert.Equal(ApiStatus.SyncDisabled, response.Status);
        Assert.True(ApiStatusMap.ShouldBackOff(response.Status));
        Assert.Equal(TimeSpan.FromSeconds(3600), response.RetryAfter);
    }

    [Fact]
    public async Task A_500_is_retryable_because_writes_are_idempotent()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.InternalServerError, ""));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.ServerError, response.Status);
        Assert.True(ApiStatusMap.IsRetryable(response.Status));
    }

    // A proxy that cannot reach the server answers with an HTML page and its own 5xx, sometimes a
    // nonstandard one such as 530. The real number is kept for diagnostics.
    [Fact]
    public async Task A_proxy_5xx_with_an_html_body_is_a_retryable_server_error()
    {
        var (client, _) = Build(_ => new HttpResponseMessage((HttpStatusCode)530)
        {
            Content = new StringContent(
                "<html><body>The server could not be reached.</body></html>", Encoding.UTF8, "text/html"),
        });

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.ServerError, response.Status);
        Assert.True(ApiStatusMap.IsRetryable(response.Status));
        Assert.Equal(530, response.HttpStatusCode);
        Assert.Null(response.Error);
    }

    [Fact]
    public async Task A_transport_failure_becomes_a_network_error_rather_than_an_exception()
    {
        var (client, _) = Build(_ => throw new HttpRequestException("dns exploded"));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NetworkError, response.Status);
        Assert.True(ApiStatusMap.IsRetryable(response.Status));

        // No headers arrived, so there is no status code to keep.
        Assert.Null(response.HttpStatusCode);
    }

    // A timeout surfaces as TaskCanceledException while the caller's token is not canceled.
    [Fact]
    public async Task A_timeout_becomes_a_network_error()
    {
        var (client, _) = Build(_ => throw new TaskCanceledException("timed out"));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NetworkError, response.Status);
        Assert.Null(response.HttpStatusCode);
    }

    [Fact]
    public async Task A_real_cancellation_propagates_instead_of_being_swallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var (client, _) = Build(_ => throw new TaskCanceledException("canceled"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetMeAsync(cts.Token));
    }

    [Fact]
    public async Task A_200_with_an_unparseable_body_is_not_treated_as_success()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.OK, "this is not json"));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.Unknown, response.Status);
        Assert.Null(response.Value);
        Assert.False(response.IsSuccess);
    }

    // Building the URL from the base's scheme, host and port alone is the sole defense against a
    // configured base URL redirecting requests somewhere unintended.
    [Fact]
    public async Task A_stray_path_in_the_base_url_cannot_rewrite_the_endpoint()
    {
        var (client, handler) = Build(
            _ => Json(HttpStatusCode.OK, """{"characters": [], "user": {"id": "abc"}}"""),
            baseUrl: "https://xiv-shinies.com/some/stray/path");

        await client.GetMeAsync();

        Assert.Equal("https://xiv-shinies.com/api/plugin/v1/me",
            handler.LastRequest!.RequestUri!.ToString());
    }

    // Retry-After may be an HTTP-date or a delta. Missing the date form would drop the backoff
    // whenever a proxy rewrites the header.
    [Fact]
    public async Task Retry_after_is_understood_in_its_http_date_form()
    {
        var (client, _) = Build(_ =>
        {
            var response = Json((HttpStatusCode)429, """{"error": "rate_limited"}""");
            response.Headers.RetryAfter =
                new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(60));
            return response;
        });

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.RateLimited, response.Status);
        Assert.NotNull(response.RetryAfter);
        // Slack for the clock ticking between building the header and reading it.
        Assert.InRange(response.RetryAfter!.Value, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60));
    }

    // The token goes to whatever host is configured, so a custom backend must be acknowledged first.
    // Enforced in the send path, not just the UI.
    [Fact]
    public async Task An_unacknowledged_custom_backend_never_receives_the_token()
    {
        var (client, handler) = Build(
            _ => Json(HttpStatusCode.OK, "{}"),
            baseUrl: "https://someone-elses-server.example",
            customBackendAcknowledged: false);

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NotConfigured, response.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task An_acknowledged_custom_backend_is_allowed()
    {
        var (client, handler) = Build(
            _ => Json(HttpStatusCode.OK, """{"characters": [], "user": {"id": "abc"}}"""),
            baseUrl: "https://someone-elses-server.example",
            customBackendAcknowledged: true);

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.Ok, response.Status);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("https://someone-elses-server.example/api/plugin/v1/me",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task The_default_backend_needs_no_acknowledgment()
    {
        var (client, handler) = Build(
            _ => Json(HttpStatusCode.OK, """{"characters": [], "user": {"id": "abc"}}"""),
            customBackendAcknowledged: false);

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.Ok, response.Status);
        Assert.Equal(1, handler.CallCount);
    }

    // Unloading the plugin cancels in-flight work. An ObjectDisposedException must never escape into
    // the game.
    [Fact]
    public async Task A_request_after_dispose_is_a_network_error_not_an_exception()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.OK, "{}"));
        client.Dispose();

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NetworkError, response.Status);
    }

    [Fact]
    public async Task A_retry_after_date_in_the_past_clamps_to_zero()
    {
        var (client, _) = Build(_ =>
        {
            var response = Json(HttpStatusCode.ServiceUnavailable, """{"error": "sync_disabled"}""");
            response.Headers.RetryAfter =
                new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-60));
            return response;
        });

        var response = await client.GetConfigAsync();

        Assert.Equal(TimeSpan.Zero, response.RetryAfter);
    }

    // Mirrors ApiClient.MaxResponseBytes, which stays private. Real responses are kilobytes; the cap
    // keeps a hostile backend from exhausting the game's memory.
    private const int ResponseByteCap = 4 * 1024 * 1024;

    // A body with no Content-Length, as a hostile or chunked-transfer server sends one, so the client
    // cannot check its size from the headers.
    private sealed class UnadvertisedStreamingContent : HttpContent
    {
        private readonly int totalBytes;

        public UnadvertisedStreamingContent(int totalBytes) => this.totalBytes = totalBytes;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            // Transport-sized chunks, as a socket delivers them.
            var chunk = new byte[81920];
            var remaining = totalBytes;
            while (remaining > 0)
            {
                var count = Math.Min(chunk.Length, remaining);
                await stream.WriteAsync(chunk.AsMemory(0, count));
                remaining -= count;
            }
        }

        // False omits the Content-Length header.
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    // A body whose connection breaks after the headers arrive. It either drops, failing every read
    // with an IOException as a broken socket does, or stalls until the client's async read is
    // canceled. Members an unseekable read-only body never needs throw, except Flush, which is legal
    // on one and does nothing.
    private sealed class BrokenBodyStream : Stream
    {
        private readonly bool stalls;

        public BrokenBodyStream(bool stalls) => this.stalls = stalls;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("The connection was reset.");

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!stalls)
                throw new IOException("The connection was reset.");

            // As on a silent connection, only a deadline or a cancellation ends the wait.
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A dropped body read throws IOException, not HttpRequestException, and nothing may throw out of
    // the client's methods. The status code that did arrive is kept.
    [Fact]
    public async Task A_connection_dropped_mid_body_is_a_network_error_not_an_exception()
    {
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BrokenBodyStream(stalls: false)),
        });

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NetworkError, response.Status);
        Assert.Equal(200, response.HttpStatusCode);
    }

    // The request's deadline covers the body, so a silent connection cannot hold the request open.
    [Fact]
    public async Task A_body_that_stalls_past_the_deadline_is_a_network_error()
    {
        var (client, _) = Build(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BrokenBodyStream(stalls: true)),
            },
            requestTimeout: TimeSpan.FromMilliseconds(200));

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NetworkError, response.Status);
        Assert.Equal(200, response.HttpStatusCode);
    }

    [Fact]
    public async Task A_caller_cancellation_during_the_body_read_propagates()
    {
        using var cts = new CancellationTokenSource();
        var (client, _) = Build(_ =>
        {
            // Canceled after the headers return, so it ends the body read.
            cts.CancelAfter(TimeSpan.FromMilliseconds(100));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BrokenBodyStream(stalls: true)),
            };
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMeAsync(cts.Token));
    }

    // The oversized body is advertised, so the client refuses it before reading a body byte.
    [Fact]
    public async Task A_response_advertising_an_oversized_body_is_refused_from_its_headers()
    {
        var (client, _) = Build(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(new byte[16])),
            };
            // The body stays tiny, so the refusal can only come from the header.
            response.Content.Headers.ContentLength = ResponseByteCap + 1L;
            return response;
        });

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NetworkError, response.Status);
        Assert.Equal(200, response.HttpStatusCode);
    }

    // With no Content-Length, the capped read must stop rather than buffer whatever arrives.
    [Fact]
    public async Task A_response_streaming_past_the_cap_without_a_length_is_refused()
    {
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnadvertisedStreamingContent(ResponseByteCap + 16),
        });

        var response = await client.GetMeAsync();

        Assert.Equal(ApiStatus.NetworkError, response.Status);
        Assert.Equal(200, response.HttpStatusCode);
    }
}
