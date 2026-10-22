using System.Text.Json;
using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The 200 body of POST /api/plugin/v1/crucible/observations: one of four outcomes, each with the
// fields the contract gives it.
public class CrucibleObservationsResponseTests
{
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

    [Fact]
    public void A_held_upload_reads_with_no_run()
    {
        var response = Parse("""{"ok":true,"outcome":"held","runId":null}""");

        Assert.Equal(CrucibleOutcomes.Held, response.Outcome);
        Assert.Null(response.RunId);
        Assert.Null(response.Events);
    }

    // The entrance's windows are held, never applied, even while a run is active.
    [Fact]
    public void An_entrance_upload_held_during_a_run_reads_that_run()
    {
        var response = Parse("""{"ok":true,"outcome":"held","runId":"0f1e2d3c"}""");

        Assert.Equal(CrucibleOutcomes.Held, response.Outcome);
        Assert.Equal("0f1e2d3c", response.RunId);
        Assert.Null(response.Events);
    }

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

    // Ignoring unknown fields lets the server add one without breaking the plugin.
    [Fact]
    public void An_unknown_field_is_ignored()
    {
        var response = Parse("""{"ok":true,"outcome":"held","runId":null,"note":"anything"}""");

        Assert.Equal(CrucibleOutcomes.Held, response.Outcome);
    }

    private static CrucibleObservationsResponse Parse(string json) =>
        JsonSerializer.Deserialize<CrucibleObservationsResponse>(json, ApiJson.Options)!;
}
