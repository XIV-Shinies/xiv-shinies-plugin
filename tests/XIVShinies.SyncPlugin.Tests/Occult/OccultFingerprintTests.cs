using System;
using Xunit;
using XIVShinies.SyncPlugin.Occult;

namespace XIVShinies.SyncPlugin.Tests.Occult;

// Whether a snapshot can identify its instance to the server. The FATE table and CE deadlines sync
// over the first seconds after zoning in, and an upload sent before they land can match nothing,
// splitting one instance across two tracker rows. The uploader holds its enter until this says go,
// up to a bounded settle window.
public class OccultFingerprintTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.FromUnixTimeSeconds(1786465621);

    private static OccultEncounterState State(
        OccultEncounterStatus status, DateTimeOffset? since, bool isFate = false, ushort id = 46) =>
        new() { IsFate = isFate, Id = id, Status = status, SinceUtc = since };

    [Fact]
    public void An_empty_snapshot_is_not_fingerprintable()
    {
        Assert.False(OccultFingerprint.IsFingerprintable([]));
    }

    [Fact]
    public void Idle_ces_with_no_timestamps_are_not_fingerprintable()
    {
        Assert.False(OccultFingerprint.IsFingerprintable(
            [State(OccultEncounterStatus.Down, since: null)]));
    }

    // A down stamp is the plugin's own observation time, not a server-assigned epoch, so it
    // anchors no match.
    [Fact]
    public void A_down_stamp_is_not_a_fingerprint()
    {
        Assert.False(OccultFingerprint.IsFingerprintable(
            [State(OccultEncounterStatus.Down, since: Epoch)]));
    }

    [Fact]
    public void An_active_entry_with_a_server_epoch_is_a_fingerprint()
    {
        Assert.True(OccultFingerprint.IsFingerprintable(
            [State(OccultEncounterStatus.Active, since: Epoch, isFate: true, id: 1972)]));
    }

    [Fact]
    public void A_preparing_ce_with_its_deadline_is_a_fingerprint()
    {
        Assert.True(OccultFingerprint.IsFingerprintable(
            [State(OccultEncounterStatus.Preparing, since: Epoch)]));
    }

    // The usual mid-sync shape: a non-idle entry whose deadline has not synced yet.
    [Fact]
    public void An_active_entry_whose_timestamp_has_not_synced_is_not_a_fingerprint()
    {
        Assert.False(OccultFingerprint.IsFingerprintable(
            [State(OccultEncounterStatus.Active, since: null)]));
    }

    [Fact]
    public void One_fingerprintable_entry_among_idle_ones_is_enough()
    {
        Assert.True(OccultFingerprint.IsFingerprintable(
        [
            State(OccultEncounterStatus.Down, since: null),
            State(OccultEncounterStatus.Down, since: null, id: 47),
            State(OccultEncounterStatus.Active, since: Epoch, isFate: true, id: 1972),
        ]));
    }
}
