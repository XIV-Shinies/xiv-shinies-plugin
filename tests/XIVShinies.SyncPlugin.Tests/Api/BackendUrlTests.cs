using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// The backend URL is user-overridable (Dalamud recommends offering a user-defined server), but
// an override must never silently downgrade the connection to plaintext. Rule: https:// always,
// except http:// is tolerated for loopback so local development works.
public class BackendUrlTests
{
    [Fact]
    public void Default_is_the_official_https_host()
    {
        Assert.Equal("https://xiv-shinies.com", BackendUrl.Default);
        Assert.True(BackendUrl.TryNormalize(BackendUrl.Default, out _, out _));
    }

    [Theory]
    [InlineData("https://xiv-shinies.com")]
    [InlineData("https://staging.xiv-shinies.com")]
    [InlineData("http://localhost:8000")]   // loopback, addressed by DNS name
    [InlineData("https://xiv-shinies.com/")] // trailing slash tolerated
    [InlineData("  https://xiv-shinies.com  ")] // surrounding whitespace trimmed
    public void Accepts_https_anywhere_and_http_on_loopback_by_name(string raw)
    {
        Assert.True(BackendUrl.TryNormalize(raw, out var uri, out var error));
        Assert.NotNull(uri);
        Assert.Null(error);
    }

    [Fact]
    public void Recognizes_the_official_server_regardless_of_path_or_case()
    {
        Assert.True(BackendUrl.TryNormalize("https://XIV-Shinies.com/ignored/path", out var uri, out _));
        Assert.True(BackendUrl.IsDefault(uri!));

        Assert.True(BackendUrl.TryNormalize("https://staging.xiv-shinies.com", out var other, out _));
        Assert.False(BackendUrl.IsDefault(other!));
    }

    // Lookalike hosts must never be mistaken for loopback and allowed over plaintext.
    [Theory]
    [InlineData("http://localhost.evil.com")]
    [InlineData("http://127.0.0.1.evil.com")]
    [InlineData("http://localhost@evil.com")] // caught by the userinfo gate before the loopback rule
    public void Rejects_loopback_lookalike_hosts_over_plaintext(string raw)
    {
        Assert.False(BackendUrl.TryNormalize(raw, out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("http://xiv-shinies.com")]  // plaintext to a remote host — the downgrade we block
    [InlineData("http://example.com:8080")]
    public void Rejects_plaintext_http_to_a_remote_host(string raw)
    {
        Assert.False(BackendUrl.TryNormalize(raw, out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("xiv-shinies.com")]      // no scheme — not an absolute URI
    [InlineData("ftp://xiv-shinies.com")] // wrong scheme entirely
    public void Rejects_blank_relative_and_non_http_urls(string? raw)
    {
        Assert.False(BackendUrl.TryNormalize(raw, out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    // Dalamud requires a backend be reached by DNS hostname, never a raw IP address, and states
    // no exemption — so even loopback IPs are refused. Use "localhost" instead of 127.0.0.1.
    [Theory]
    [InlineData("https://203.0.113.5")]      // remote IPv4
    [InlineData("https://203.0.113.5:8443")]
    [InlineData("https://[2001:db8::1]")]    // remote IPv6
    [InlineData("http://127.0.0.1:8000")]    // loopback IPv4 — still an IP address
    [InlineData("https://127.0.0.1:8000")]
    [InlineData("http://[::1]:8000")]        // loopback IPv6
    public void Rejects_every_raw_ip_address_including_loopback(string raw)
    {
        Assert.False(BackendUrl.TryNormalize(raw, out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    // The obscure single-number spellings of an IPv4 address: Uri classifies these as DNS names,
    // but the OS resolver treats them as addresses — both are 127.0.0.1 here. "Raw IP refused"
    // must hold for every spelling of an address, not just the dotted one.
    [Theory]
    [InlineData("https://2130706433")]  // 127.0.0.1 as one decimal number
    [InlineData("https://0x7f000001")]  // 127.0.0.1 in hex
    public void Rejects_numerically_encoded_ip_addresses(string raw)
    {
        Assert.False(BackendUrl.TryNormalize(raw, out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    // --- The host named in an unreachable message -----------------------------------------------

    // Every sentence naming the service names the configured one — see BackendUrl.DisplayHost.

    [Fact]
    public void The_official_backend_is_named_by_its_host()
    {
        Assert.Equal("xiv-shinies.com", BackendUrl.DisplayHost(BackendUrl.Default));
    }

    [Fact]
    public void A_custom_backend_is_named_by_its_own_host()
    {
        Assert.Equal(
            "dev.example.com", BackendUrl.DisplayHost("https://dev.example.com/api"));
    }

    // The host alone, because this lands mid-sentence — a scheme, a port and a path read as debris
    // there, and the host is the part that identifies the server.
    [Fact]
    public void Only_the_host_is_named_not_the_whole_url()
    {
        Assert.Equal("localhost", BackendUrl.DisplayHost("https://localhost:5173/api/plugin/v1"));
    }

    // A stored value too malformed to parse has no host to show. Naming the official server is the
    // honest fallback: printing raw config text would put unparsed settings on screen.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/only")]
    public void An_unusable_setting_falls_back_to_the_official_host(string? raw)
    {
        Assert.Equal("xiv-shinies.com", BackendUrl.DisplayHost(raw));
    }

    // The values that PARSE but are refused, which is the class a "does it parse" check would let
    // through. Naming one of these would promise a destination the client will not send to: a
    // refused address means every request answers NotConfigured, so the privacy cards would state
    // a recipient that never receives anything.
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://[::1]")]
    [InlineData("ftp://example.com")]
    [InlineData("file://server/share")]
    [InlineData("mailto:someone@example.com")]
    public void A_setting_the_client_refuses_to_use_is_never_named(string raw)
    {
        Assert.Equal("xiv-shinies.com", BackendUrl.DisplayHost(raw));
    }

    // The named host and the opened link answer to one gate, so the sentence and the button beneath
    // it can never describe different servers.
    [Theory]
    [InlineData("https://dev.example.com")]
    [InlineData("http://localhost:5173")]
    [InlineData("https://127.0.0.1")]
    [InlineData("http://example.com")]
    [InlineData("not a url")]
    public void The_named_host_and_the_profile_link_always_agree(string raw)
    {
        // Whichever server DisplayHost names is the one ProfileUrl opens, fallback included.
        Assert.Equal(BackendUrl.DisplayHost(raw), new Uri(BackendUrl.ProfileUrl(raw)).IdnHost);
    }

    // An international host is named in the punycode form, which cannot impersonate another domain.
    [Fact]
    public void An_international_host_is_named_in_punycode()
    {
        Assert.Equal(
            "xn--e1afmkfd.example",
            BackendUrl.DisplayHost("https://\u043F\u0440\u0438\u043C\u0435\u0440.example"));
    }

    // --- Credentials in the URL ------------------------------------------------------------------

    // A URL carrying credentials is refused outright — see the userinfo gate in TryNormalize.
    [Fact]
    public void A_url_carrying_credentials_is_refused()
    {
        Assert.False(
            BackendUrl.TryNormalize("https://user:pass@dev.example.com", out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    // The impersonation this closes: the official name sits where the eye lands first, while the
    // host that would actually be opened is something else entirely.
    [Fact]
    public void A_url_impersonating_the_official_host_through_credentials_is_refused()
    {
        Assert.False(BackendUrl.TryNormalize("https://xiv-shinies.com@evil.example", out _, out _));
        Assert.Equal("xiv-shinies.com", BackendUrl.DisplayHost("https://xiv-shinies.com@evil.example"));
        Assert.Equal(
            "https://xiv-shinies.com/profile",
            BackendUrl.ProfileUrl("https://xiv-shinies.com@evil.example"));
    }

    // --- Where the Open profile button goes -----------------------------------------------------

    // The link follows the configured backend, and is validated before it is opened — see ProfileUrl.

    [Fact]
    public void The_profile_page_is_on_the_official_server_by_default()
    {
        Assert.Equal("https://xiv-shinies.com/profile", BackendUrl.ProfileUrl(BackendUrl.Default));
    }

    [Fact]
    public void The_profile_page_follows_a_custom_backend()
    {
        Assert.Equal(
            "https://dev.example.com/profile", BackendUrl.ProfileUrl("https://dev.example.com"));
    }

    // A base URL carrying a path is still only a server address here — the profile page lives at
    // the root, so an api prefix must not end up inside the link.
    [Fact]
    public void A_base_url_with_a_path_still_points_at_the_profile_root()
    {
        Assert.Equal(
            "https://dev.example.com/profile", BackendUrl.ProfileUrl("https://dev.example.com/api/"));
    }

    // The port belongs to the server, so a local backend keeps it.
    [Fact]
    public void A_local_backend_keeps_its_port()
    {
        Assert.Equal("http://localhost:5173/profile", BackendUrl.ProfileUrl("http://localhost:5173"));
    }

    // The stored setting can be hand-edited and so need never have passed validation. Anything that
    // fails the scheme and hostname rules falls back to the official site rather than being opened:
    // this value is handed to the operating system, so a non-web scheme must never reach it.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("file:///C:/Windows/System32")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.com")]
    [InlineData("https://127.0.0.1")]
    public void An_unusable_or_disallowed_setting_falls_back_to_the_official_profile(string? raw)
    {
        Assert.Equal("https://xiv-shinies.com/profile", BackendUrl.ProfileUrl(raw));
    }

    // --- Why a configured server cannot be contacted ---------------------------------------------

    // Two different settings produce the same refusal and need opposite actions, so the sentence has
    // to tell them apart — see BackendUrl.DescribeUnusableSetting.

    // A broken address borrows TryNormalize's own complaint, so the user reads the rule they broke
    // rather than a generic one.
    [Fact]
    public void A_malformed_address_is_explained_by_the_rule_it_breaks()
    {
        BackendUrl.TryNormalize("http://example.com", out _, out var rule);
        var sentence = BackendUrl.DescribeUnusableSetting("http://example.com", true);

        Assert.NotNull(rule);
        Assert.Contains(rule, sentence);
    }

    // The case a developer actually hits: the address is fine and the acknowledgment is missing.
    // Telling them to fix the address would send them looking for a fault that is not there.
    [Fact]
    public void An_unacknowledged_custom_server_is_not_reported_as_a_bad_address()
    {
        var sentence = BackendUrl.DescribeUnusableSetting("https://dev.example.com", false);

        Assert.Contains("acknowledged", sentence);
        Assert.DoesNotContain("cannot be used", sentence);
    }

    // The official server needs no acknowledgment, so the flag cannot be the explanation there.
    [Fact]
    public void The_official_server_is_never_reported_as_unacknowledged()
    {
        Assert.DoesNotContain(
            "acknowledged", BackendUrl.DescribeUnusableSetting(BackendUrl.Default, false));
    }

    // Neither setting explains it, so the sentence claims only what is certain rather than guessing.
    [Fact]
    public void A_usable_acknowledged_setting_gets_no_invented_cause()
    {
        var sentence = BackendUrl.DescribeUnusableSetting("https://dev.example.com", true);

        Assert.DoesNotContain("acknowledged", sentence);
        Assert.DoesNotContain("cannot be used", sentence);
    }

    // The window's cached copy of this answer uses "empty" to mean "not yet asked", which is only
    // sound while no input can produce an empty answer — pinned here so the two files cannot
    // drift apart silently.
    [Theory]
    [InlineData(BackendUrl.Default)]
    [InlineData("https://dev.example.com")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://example.com")]
    public void The_named_host_is_never_empty(string? raw)
    {
        Assert.False(string.IsNullOrEmpty(BackendUrl.DisplayHost(raw)));
    }
}
