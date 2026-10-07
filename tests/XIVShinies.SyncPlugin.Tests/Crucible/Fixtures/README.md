# Crucible window fixtures

Sample values of the Crucible of the Unbroken's windows (boards 1 and 2, Standard, patch 7.56),
for the plugin's window readers. Each file is one window's backing values, the array the game
hands a window to draw itself from, at one moment:

```json
{ "window": "XBMContentsMainHUD", "event": "PostRefresh", "valueCount": 111,
  "values": [ { "i": 8, "type": "String", "value": "225" } ] }
```

`window` is the game's name for the window and `event` the moment the values belong to (the
window's setup, or a redraw). `valueCount` is how many values the window has, `i` is each listed
value's position, and `type` its kind (`Bool`, `UInt`, `Int` or a string kind). The loader builds
a list of `valueCount` values in which every position not listed loads as unreadable.

## What the files hold

- The values the readers use, plus a few layout values for context, such as a board piece's kind
  and a familiar record's feed icons. Interface text, descriptions, item and gear names, and stats
  other than HP are not listed.
- Enemy rows in the board files hold only their record type and the piece they belong to; their
  names, stats and weaknesses are not listed. That is all a test of skipping them by type needs.

Text is the game's raw string: the team window's rank labels carry their private-use paw glyph
(U+E036). None of the listed values carries a formatting code, so code handling is tested with
strings written into `CrucibleTextTests` and `CrucibleBoardTests`. Nothing in these files
identifies a player.

## The files

| File | Window | What it shows |
| --- | --- | --- |
| `hud.json` | `XBMContentsMainHUD` | the run HUD: tokens, three items, two pieces of gear |
| `treasure.json` | `XBMContentsTreasure` | a treasure coffer's four offers |
| `booty.json` | `XBMContentsBooty` | a fight's loot, with the tokens it paid |
| `shop.json` | `XBMContentsItemShop` | a shop with discounted and bought offers |
| `petparty-mode0-roster-stale.json` | `XBMPetParty` | the roster pick, with a stale place past the count |
| `petparty-mode1-browse-downed.json` | `XBMPetParty` | browse, with a downed familiar |
| `petparty-mode2-lineup-picked.json` | `XBMPetParty` | a lineup with three places picked |
| `petparty-mode3-feed.json` | `XBMPetParty` | a feed window, and a familiar already carrying a feed |
| `petparty-mode4-campsite-resting.json` | `XBMPetParty` | a campsite with a familiar picked to rest |
| `petparty-mode5-blessed-horn.json` | `XBMPetParty` | the Blessed Horn mode |
| `stage-view0-pre-entry-board1.json` | `XBMStageDetailList` | the whole board before entering |
| `stage-view1-midrun-progress-board1.json` | `XBMStageDetailList` | Board Layout mid-run |
| `stage-view1-expanded-enemies-board1.json` | `XBMStageDetailList` | Board Layout, the boss fight expanded |
| `stage-view1-midrun-random-board2.json` | `XBMStageDetailList` | board 2 mid-run, its random piece cleared |
| `stage-view2-scoped-board1.json` | `XBMStageDetailList` | the view scoped to one fight piece |
| `result-board1-willing-sacrifice.json` | `XBMResult` | board 1's results screen with one bonus |
| `result-board2.json` | `XBMResult` | board 2's results screen |

A patch that changes a window's layout makes these stale: a reader refuses values whose layout
has moved rather than misreading them, so the files change with the layout.
