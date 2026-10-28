using System.Text.Json;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Which of an upload's sent facts its response lets the plugin forget.
public class StoreConfirmationsTests
{
    private static readonly StoreConfirmation Facewear = new("storedFacewear", () => { });
    private static readonly StoreConfirmation Ornaments = new("storedOrnaments", () => { });

    private static SyncResponse Response(string json) =>
        JsonSerializer.Deserialize<SyncResponse>(json, ApiJson.Options)!;

    [Fact]
    public void A_category_whose_key_the_response_carries_is_confirmed()
    {
        var confirmed = StoreConfirmations.Confirmed(
            new[] { Facewear, Ornaments },
            Response("""{"ok": true, "bound": false, "storedFacewear": 2}"""));

        Assert.Equal(new[] { Facewear }, confirmed);
    }

    // The key is missing when the store failed or the server stripped the category, so the facts stay
    // held for the next upload.
    [Fact]
    public void A_response_without_the_key_confirms_nothing()
    {
        Assert.Empty(StoreConfirmations.Confirmed(
            new[] { Facewear }, Response("""{"ok": true, "bound": false}""")));
    }

    [Fact]
    public void A_missing_response_body_confirms_nothing()
    {
        Assert.Empty(StoreConfirmations.Confirmed(new[] { Facewear }, null));
    }
}
