using System.Text.Json;
using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The 200 body of POST /api/plugin/v1/crucible/observations: one of four outcomes, each with the
// fields the contract gives it.
public class CrucibleObservationsResponseTests
{
    // Each body is a `"""` raw string: everything between the triple quotes is literal text, so
    // the JSON's own quotes need no escaping.
    [Fact]
    public void An_applied_upload_reads_its_run_and_the_events_it_wrote()
    {
        var response = Parse(
            """{"ok":true,"outcome":"applied","runId":"0f1e2d3c","events":3,"skipped":1}""");

        Assert.True(response.Ok);
        Assert.Equal(CrucibleOutcomes.Applied, response.Outcome);
        Assert.Equal("0f1e2d3c", response.RunId);
        Assert.Equal(3, response.Events);
        Assert.Equal(1, response.Skipped);
        Assert.Null(response.BoardId);
    }

    // Held: no run is active yet, so there is no run id.
    [Fact]
    public void A_held_upload_reads_with_no_run()
    {
        var response = Parse("""{"ok":true,"outcome":"held","runId":null}""");

        Assert.Equal(CrucibleOutcomes.Held, response.Outcome);
        Assert.Null(response.RunId);
        Assert.Null(response.Events);
    }

    // The website's run is on another board, which the response names.
    [Fact]
    public void A_board_mismatch_reads_the_board_the_run_is_on()
    {
        var response = Parse(
            """{"ok":true,"outcome":"board_mismatch","runId":"0f1e2d3c","boardId":2}""");

        Assert.Equal(CrucibleOutcomes.BoardMismatch, response.Outcome);
        Assert.Equal(2, response.BoardId);
    }

    [Fact]
    public void A_leave_reads_as_left()
    {
        var response = Parse("""{"ok":true,"outcome":"left","runId":null}""");

        Assert.Equal(CrucibleOutcomes.Left, response.Outcome);
    }

    // A field the plugin does not know is ignored, so the server can add one without breaking it.
    [Fact]
    public void An_unknown_field_is_ignored()
    {
        var response = Parse("""{"ok":true,"outcome":"held","runId":null,"note":"anything"}""");

        Assert.Equal(CrucibleOutcomes.Held, response.Outcome);
    }

    /// <summary>Reads a response body with the plugin's own wire settings.</summary>
    // `Deserialize<T>` parses JSON into the type named in the angle brackets, like `JSON.parse`
    // with a type; the `!` after it tells the compiler the result is not null.
    private static CrucibleObservationsResponse Parse(string json) =>
        JsonSerializer.Deserialize<CrucibleObservationsResponse>(json, ApiJson.Options)!;
}
