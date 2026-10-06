using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Beastmaster.Crucible;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

// The body of POST /api/plugin/v1/crucible/observations. The server's schema is strict: a key it
// does not name fails the whole upload, and so does a named key that is missing. So each snapshot
// and request shape test compares the whole JSON object, key for key, against the contract's own
// shape.
public class CrucibleUploadBuilderTests
{
    // `static readonly` is a value built once and never reassigned; `const` is one fixed when the
    // code compiles, which only simple values such as strings and numbers can be.

    /// <summary>A moment with a fraction of a second, which the wire format drops.</summary>
    private static readonly DateTimeOffset ObservedAt =
        new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(750);

    /// <summary>How <see cref="ObservedAt"/> reads on the wire.</summary>
    private const string ObservedAtText = "2026-09-28T12:00:00Z";

    // A board in the whole-board view names no current piece, and the key is still sent, as null.
    // `CurrentNodeIndex: null` names the argument it fills, which reads like an object literal's
    // key. A `"""` string is a raw string: everything up to the closing `"""` is literal text, so
    // the JSON needs no escaping.
    [Fact]
    public void A_whole_board_snapshot_sends_every_piece_and_a_null_current_piece()
    {
        var reading = new CrucibleBoardReading(
            CrucibleBoardView.WholeBoard,
            [
                new CrucibleBoardPiece(1, CrucibleProgress.Cleared),
                new CrucibleBoardPiece(2, CrucibleProgress.NotTaken),
                new CrucibleBoardPiece(4, CrucibleProgress.NotReached),
            ],
            CurrentNodeIndex: null,
            ModeText: "Standard");

        AssertJson(
            """
            {"kind":"board","observedAtUtc":"2026-09-28T12:00:00Z","closed":false,"view":1,
             "currentNodeIndex":null,
             "pieces":[{"nodeIndex":1,"progress":1},{"nodeIndex":2,"progress":2},
                       {"nodeIndex":4,"progress":0}]}
            """,
            CrucibleUploadBuilder.Board(reading, ObservedAt, closed: false));
    }

    // Every view goes out as the window's own number, never as the enum's name.
    [Theory]
    [InlineData(CrucibleBoardView.PreEntry, 0)]
    [InlineData(CrucibleBoardView.WholeBoard, 1)]
    [InlineData(CrucibleBoardView.Scoped, 2)]
    public void Every_board_view_is_sent_as_its_number(CrucibleBoardView view, int number)
    {
        var reading = new CrucibleBoardReading(view, [], CurrentNodeIndex: null, ModeText: null);

        // `GetValue<int>()` reads the JSON value as the type named in the angle brackets.
        Assert.Equal(number, Serialize(CrucibleUploadBuilder.Board(reading, ObservedAt, false))
            ["view"]!.GetValue<int>());
    }

    // The scoped view carries the piece being played, and a closing window says so.
    [Fact]
    public void A_scoped_board_snapshot_sends_its_current_piece_and_whether_it_closed()
    {
        var reading = new CrucibleBoardReading(
            CrucibleBoardView.Scoped,
            [new CrucibleBoardPiece(6, CrucibleProgress.NotReached)],
            CurrentNodeIndex: 6,
            ModeText: "Standard");

        AssertJson(
            """
            {"kind":"board","observedAtUtc":"2026-09-28T12:00:00Z","closed":true,"view":2,
             "currentNodeIndex":6,"pieces":[{"nodeIndex":6,"progress":0}]}
            """,
            CrucibleUploadBuilder.Board(reading, ObservedAt, closed: true));
    }

    // Outside the feed and Blessed Horn modes the window chooses for no item, sent as null.
    [Fact]
    public void A_team_snapshot_sends_each_familiar_and_a_null_item()
    {
        var reading = new CrucibleTeamReading(
            CrucibleTeamMode.Lineup,
            ItemId: null,
            [
                new CrucibleFamiliar(10, 0, 640, 667, 5, true, [], false),
                new CrucibleFamiliar(16, 3, 0, 550, 9, false, [146, 148], false),
            ]);

        AssertJson(
            """
            {"kind":"team","observedAtUtc":"2026-09-28T12:00:00Z","closed":true,"mode":2,
             "itemId":null,
             "familiars":[
               {"petId":10,"place":0,"hp":{"current":640,"max":667},"rank":5,"rankSynced":true,
                "feedItemIds":[],"resting":false},
               {"petId":16,"place":3,"hp":{"current":0,"max":550},"rank":9,"rankSynced":false,
                "feedItemIds":[146,148],"resting":false}]}
            """,
            CrucibleUploadBuilder.Team(reading, ObservedAt, closed: true));
    }

    [Theory]
    [InlineData(CrucibleTeamMode.RosterPick, 0)]
    [InlineData(CrucibleTeamMode.Browse, 1)]
    [InlineData(CrucibleTeamMode.Lineup, 2)]
    [InlineData(CrucibleTeamMode.Feed, 3)]
    [InlineData(CrucibleTeamMode.Campsite, 4)]
    [InlineData(CrucibleTeamMode.BlessedHorn, 5)]
    public void Every_team_mode_is_sent_as_its_number(CrucibleTeamMode mode, int number)
    {
        var reading = new CrucibleTeamReading(mode, ItemId: null, []);

        Assert.Equal(number, Serialize(CrucibleUploadBuilder.Team(reading, ObservedAt, false))
            ["mode"]!.GetValue<int>());
    }

    [Fact]
    public void A_feed_window_sends_the_feed_it_offers()
    {
        var reading = new CrucibleTeamReading(
            CrucibleTeamMode.Feed,
            ItemId: 148,
            [new CrucibleFamiliar(20, 3, 400, 400, 4, false, [], true)]);

        AssertJson(
            """
            {"kind":"team","observedAtUtc":"2026-09-28T12:00:00Z","closed":false,"mode":3,
             "itemId":148,
             "familiars":[{"petId":20,"place":3,"hp":{"current":400,"max":400},"rank":4,
                           "rankSynced":false,"feedItemIds":[],"resting":true}]}
            """,
            CrucibleUploadBuilder.Team(reading, ObservedAt, closed: false));
    }

    // The bag has no closed flag: the HUD stays open for the whole run.
    [Fact]
    public void A_bag_snapshot_sends_the_tokens_items_and_gear()
    {
        var reading = new CrucibleBagReading(764, [78, 130, 138], [2, 45]);

        AssertJson(
            """
            {"kind":"bag","observedAtUtc":"2026-09-28T12:00:00Z","tokens":764,
             "itemIds":[78,130,138],"gearIds":[2,45]}
            """,
            CrucibleUploadBuilder.Bag(reading, ObservedAt));
    }

    // A treasure or loot offer carries its item id alone; the shop's three fields are left out,
    // not sent as null.
    [Fact]
    public void A_treasure_snapshot_sends_item_ids_alone_and_a_null_payout()
    {
        var reading = new CrucibleOfferReading(
            CrucibleOfferSource.Treasure, 450, TokensEarned: null,
            [new CrucibleOffer(12), new CrucibleOffer(53)]);

        AssertJson(
            """
            {"kind":"offer","observedAtUtc":"2026-09-28T12:00:00Z","closed":false,
             "source":"treasure","tokens":450,"tokensEarned":null,
             "offers":[{"itemId":12},{"itemId":53}]}
            """,
            CrucibleUploadBuilder.Offer(reading, ObservedAt, closed: false));
    }

    [Fact]
    public void A_loot_snapshot_sends_the_tokens_the_fight_paid()
    {
        var reading = new CrucibleOfferReading(
            CrucibleOfferSource.Loot, 0, TokensEarned: 225, [new CrucibleOffer(63)]);

        AssertJson(
            """
            {"kind":"offer","observedAtUtc":"2026-09-28T12:00:00Z","closed":true,
             "source":"loot","tokens":0,"tokensEarned":225,"offers":[{"itemId":63}]}
            """,
            CrucibleUploadBuilder.Offer(reading, ObservedAt, closed: true));
    }

    [Fact]
    public void A_shop_snapshot_sends_each_offers_price_discount_and_purchase()
    {
        var reading = new CrucibleOfferReading(
            CrucibleOfferSource.Shop, 764, TokensEarned: null,
            [new CrucibleOffer(162, 25, true, false), new CrucibleOffer(64, 564, false, true)]);

        AssertJson(
            """
            {"kind":"offer","observedAtUtc":"2026-09-28T12:00:00Z","closed":false,
             "source":"shop","tokens":764,"tokensEarned":null,
             "offers":[{"itemId":162,"price":25,"discounted":true,"bought":false},
                       {"itemId":64,"price":564,"discounted":false,"bought":true}]}
            """,
            CrucibleUploadBuilder.Offer(reading, ObservedAt, closed: false));
    }

    // The mode, the rank and each bonus go out as the ids they resolved to, and the score keys are
    // the contract's. Every number in the sample differs from the others, so a field written under
    // the wrong key cannot pass.
    [Fact]
    public void A_results_snapshot_sends_the_resolved_ids_and_the_score()
    {
        // A `Dictionary` maps keys to values, like a TypeScript `Map`; `["key"] = value` adds an
        // entry while it is being built.
        var bonusIds = new Dictionary<string, uint> { ["Willing Sacrifice I"] = 17 };

        AssertJson(
            """
            {"kind":"results","observedAtUtc":"2026-09-28T12:00:00Z","degree":2,"rankIndex":4,
             "score":{"base":7631,"performancePct":98,"performancePoints":5003,
                      "enemies":5,"enemyPoints":1010,"elites":3,"elitePoints":625,
                      "bosses":6,"bossPoints":993,"remainingHp":462,
                      "bonusPoints":250,"total":7881},
             "bonuses":[{"bonusId":17,"points":240}],
             "familiars":[{"petId":12,"rankBefore":7,"rankAfter":8,"expBefore":65,"expAfter":61}]}
            """,
            CrucibleUploadBuilder.Results(
                SampleResults(), ObservedAt, degree: 2, rankIndex: 4, Resolve(bonusIds)));
    }

    // Text the plugin could not resolve goes out as null, never as a guess, and the key stays.
    [Fact]
    public void Unresolved_mode_rank_and_bonus_names_are_sent_as_null()
    {
        var json = Serialize(CrucibleUploadBuilder.Results(
            SampleResults(), ObservedAt, degree: null, rankIndex: null,
            Resolve(new Dictionary<string, uint>())));

        Assert.True(json.ContainsKey("degree"));
        Assert.Null(json["degree"]);
        Assert.True(json.ContainsKey("rankIndex"));
        Assert.Null(json["rankIndex"]);

        var bonus = json["bonuses"]!.AsArray()[0]!.AsObject();
        Assert.True(bonus.ContainsKey("bonusId"));
        Assert.Null(bonus["bonusId"]);
    }

    [Fact]
    public void A_self_snapshot_sends_the_characters_own_hp()
    {
        AssertJson(
            """
            {"kind":"self","observedAtUtc":"2026-09-28T12:00:00Z",
             "hp":{"current":1180,"max":1300}}
            """,
            CrucibleUploadBuilder.Self(1180, 1300, ObservedAt));
    }

    // The envelope carries the identity fields /sync uses, the trigger word and the territory,
    // and the observations in the order given.
    [Fact]
    public void A_request_carries_the_identity_trigger_territory_and_observations_in_order()
    {
        var request = CrucibleUploadBuilder.Request(
            new string('a', 64), "Some Name", "Excalibur", "0.9.0",
            CrucibleTrigger.Change, territoryTypeId: 1339,
            [
                CrucibleUploadBuilder.Self(1180, 1300, ObservedAt),
                CrucibleUploadBuilder.Bag(new CrucibleBagReading(764, [78], []), ObservedAt),
            ]);

        AssertJson(
            // `$$$"""` marks an interpolated raw string whose holes are written `{{{ }}}`, so the
            // JSON's own `{` and `}}` stay plain text.
            $$$"""
            {"characterContentIdHash":"{{{new string('a', 64)}}}","characterName":"Some Name",
             "homeWorld":"Excalibur","pluginVersion":"0.9.0","trigger":"change",
             "territoryTypeId":1339,
             "observations":[
               {"kind":"self","observedAtUtc":"{{{ObservedAtText}}}",
                "hp":{"current":1180,"max":1300}},
               {"kind":"bag","observedAtUtc":"{{{ObservedAtText}}}","tokens":764,"itemIds":[78],
                "gearIds":[]}]}
            """,
            request);
    }

    // The request keeps its own copy of the list, so clearing the caller's list leaves the request
    // unchanged.
    [Fact]
    public void A_request_keeps_its_own_copy_of_the_observations()
    {
        var pending = new List<CrucibleObservation> { CrucibleUploadBuilder.Self(1180, 1300, ObservedAt) };
        var request = CrucibleUploadBuilder.Request(
            new string('a', 64), "Some Name", "Excalibur", "0.9.0",
            CrucibleTrigger.Change, territoryTypeId: 1339, pending);

        pending.Clear();

        Assert.Single(request.Observations);
    }

    // A heartbeat or a leave carries no observations, and the empty list is still sent.
    [Theory]
    [InlineData(CrucibleTrigger.Enter, "enter")]
    [InlineData(CrucibleTrigger.Change, "change")]
    [InlineData(CrucibleTrigger.Heartbeat, "heartbeat")]
    [InlineData(CrucibleTrigger.Leave, "leave")]
    public void Every_trigger_serializes_as_its_contract_word(CrucibleTrigger trigger, string word)
    {
        var json = Serialize(CrucibleUploadBuilder.Request(
            new string('a', 64), "Some Name", "Excalibur", "0.9.0", trigger, 1339, []));

        Assert.Equal(word, json["trigger"]!.GetValue<string>());
        Assert.Empty(json["observations"]!.AsArray());
    }

    // Every list a kind defines goes up even when it holds nothing, as an empty array.
    [Fact]
    public void An_empty_list_is_sent_as_an_empty_array()
    {
        // Each case is a snapshot and the list keys it must carry. `(Type Name, Type Name)[]` is an
        // array of tuples with named parts, like TypeScript's `[snapshot: X, lists: string[]][]`.
        (CrucibleObservation Snapshot, string[] Lists)[] cases =
        [
            (CrucibleUploadBuilder.Board(
                new CrucibleBoardReading(CrucibleBoardView.WholeBoard, [], null, null), ObservedAt, false),
             ["pieces"]),
            (CrucibleUploadBuilder.Team(
                new CrucibleTeamReading(CrucibleTeamMode.Lineup, null, []), ObservedAt, false),
             ["familiars"]),
            (CrucibleUploadBuilder.Bag(new CrucibleBagReading(0, [], []), ObservedAt),
             ["itemIds", "gearIds"]),
            (CrucibleUploadBuilder.Offer(
                new CrucibleOfferReading(CrucibleOfferSource.Treasure, 0, null, []), ObservedAt, false),
             ["offers"]),
            // `with { ... }` copies a record with the listed properties replaced, like
            // `{ ...SampleResults(), bonuses: [], familiars: [] }`.
            (CrucibleUploadBuilder.Results(
                SampleResults() with { Bonuses = [], Familiars = [] },
                ObservedAt,
                degree: null,
                rankIndex: null,
                bonusId: _ => null),
             ["bonuses", "familiars"]),
        ];

        // `var (snapshot, lists)` takes each tuple apart into two variables, by position, like
        // `for (const [snapshot, lists] of cases)`.
        foreach (var (snapshot, lists) in cases)
        {
            var json = Serialize(snapshot);

            // `Assert.IsType<JsonArray>` fails on a missing key or a non-array, and hands back the
            // array when it passes.
            foreach (var list in lists)
                Assert.Empty(Assert.IsType<JsonArray>(json[list]));
        }
    }

    /// <summary>A results reading whose numbers all differ from one another.</summary>
    // `new(...)` with no type name builds the type the method returns, here with every argument
    // named.
    private static CrucibleResultsReading SampleResults() =>
        new(
            ModeText: "Standard",
            BaseScore: 7631,
            PerformancePercent: 98,
            PerformancePoints: 5003,
            Enemies: new CrucibleScoreLine(5, 1010),
            Elites: new CrucibleScoreLine(3, 625),
            Bosses: new CrucibleScoreLine(6, 993),
            RemainingHp: 462,
            BonusTotal: 250,
            TotalScore: 7881,
            RankText: "Exemplary",
            Bonuses: [new CrucibleResultBonus("Willing Sacrifice I", 240)],
            Familiars: [new CrucibleResultFamiliar(12, 7, 8, 65, 61)]);

    /// <summary>A bonus-name lookup over a fixed table; a name the table lacks resolves to null.</summary>
    // `TryGetValue(name, out var id)` returns whether the key is there and, when it is, writes its
    // value into the new variable `id`: an `out` argument is how a method hands back a second
    // value beside its return value.
    private static Func<string, uint?> Resolve(Dictionary<string, uint> table) =>
        name => table.TryGetValue(name, out var id) ? id : null;

    /// <summary>Serializes a value with the plugin's own wire settings.</summary>
    // `<T>` makes this generic: it serializes as `T`, the type the argument is declared as, like a
    // TypeScript function with a type parameter. The builder hands each observation back as the base
    // `CrucibleObservation`, and serializing as that base is what writes its "kind" key.
    private static JsonObject Serialize<T>(T value) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, ApiJson.Options))!.AsObject();

    /// <summary>Asserts that a value serializes to exactly this JSON object, key for key.</summary>
    private static void AssertJson<T>(string expected, T value)
    {
        var actual = Serialize(value);

        // DeepEquals compares every key and value, ignoring the order of an object's keys but not
        // of an array's items, like a deep equality matcher. The `$"..."` message fills its `{ }`
        // holes with values, like a TypeScript template literal.
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), actual),
            $"Expected {JsonNode.Parse(expected)!.ToJsonString()}\nActual   {actual.ToJsonString()}");
    }
}
