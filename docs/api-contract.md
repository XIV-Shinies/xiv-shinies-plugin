# XIV Shinies plugin-sync API contract (client-facing)

This is the contract the **XIV Shinies Sync** plugin implements: how it authenticates and
what the `/api/plugin/v1/*` endpoints accept and return. It is the reference a contributor
uses when building or changing any request/response type.

> **Authority.** The **deployed XIV Shinies server** defines and enforces this contract; it
> is the ultimate source of truth. If the live API and this document ever disagree, the
> server wins and this document must be corrected. Anything the plugin sends or receives
> must match the server exactly — never implement payloads from memory or guess field names.
>
> This document is deliberately limited to the client-facing wire format. Server
> implementation (data model, derivation logic, internal design rationale) is intentionally
> not covered here.

## Overview

The plugin reads completion facts directly from the game client — completed quest IDs,
achievement/mount/minion unlock IDs, possession counts for server-requested items, journal
sequence positions for server-requested quests — and uploads them over HTTPS. **The server does all derivation** (quest completion, relic steps);
the plugin never computes app concepts, so new relic series, quest links, and proof rules
ship without a plugin update.

Two principles govern every upload:

- **First-party evidence.** A plugin upload comes from inside the game client, so it outranks
  a Lodestone scrape and cannot be erased by a manual unmark on the website. It does not prove
  ownership: only a character already verified on the website accepts uploads (see
  [Character binding](#character-binding)).
- **Monotonic writes.** Collections only grow. An ID absent from a snapshot means "not read
  this time" (list not loaded, category disabled) — never "lost" — so a partial upload is
  always safe. Acquisition flags are set, never auto-unset, and rows are never deleted by a
  sync. The `glamour` category is the exception by design: it reports current holdings, under
  its own rules (see its bullet below).

## Transport basics

- **Base URL:** `https://xiv-shinies.com` — user-overridable for local development.
- **`User-Agent: XIVShinies.SyncPlugin/<version>`** on every request.
- All endpoints live under `/api/plugin/v1/`. Every response body is JSON. Every
  authenticated success carries `Cache-Control: no-store` (per-token private data, and a
  cached kill switch would be a stale kill switch).
- Using the wrong method (POST to `/me` or `/config`, GET to `/sync`, `/occult/instance-state`
  or `/crucible/observations`) returns **405**.

## Authentication

Users generate a token on their XIV Shinies **profile settings page** ("Game plugin" section): the
raw token is shown **once** and never recoverable (only its hash is stored server-side).
Tokens are revocable, and revoking permanently deletes the token. Each account may hold at
most **10 tokens**.

- **Format:** `xvs_` followed by 43 base64url characters (32 random bytes). The `xvs_`
  prefix lets a leaked value be recognized; strings without it are rejected before any
  lookup.
- **Transport:** `Authorization: Bearer <token>` on every request. The scheme name is
  case-insensitive; exactly one space separates scheme and token. The plugin endpoints are
  **bearer-only** — there is no cookie-session fallback.
- **401 semantics:** every auth failure — missing header, malformed header, unknown or
  revoked token — returns the same opaque body:

  ```text
  401  {"error": "invalid_token"}    WWW-Authenticate: Bearer
  ```

  A 401 never heals on retry. On a 401 the plugin should stop syncing and tell the user to
  generate a new token.

## Endpoints

### GET /api/plugin/v1/me

The status/link probe: given only its token, the plugin learns which user it belongs to and
every character that user has **claimed** (favorites are invisible — see
[Character binding](#character-binding)).

```jsonc
200
{
  "characters": [
    {
      "id": "12345678",        // Lodestone id — a BigInt, so it travels as a string
      "name": "Some Name",
      "pluginLinked": true,    // a ContentId hash is already bound to this character
      "verified": true,        // the claim is verified by the Lodestone bio code
      "world": "Excalibur"
    }
  ],
  "user": {"id": "<user uuid>"}
}
```

Characters are ordered alphabetically by name. Statuses: **200**, **401**, **405** (non-GET).

### GET /api/plugin/v1/config

Remote config + item manifest. The plugin polls this roughly every 30 minutes. Values are
read per request, so a flipped kill switch reaches the plugin on its next poll.

```jsonc
200
{
  "categories": {              // per-category kill switches (true = enabled)
    "achievements": true,
    "appearance": true,        // must be named to be collected at all (see below)
    "glamour": true,           // must be named to be collected at all (see below)
    "items": true,
    "minions": true,
    "mounts": true,
    "occultProgression": true,
    "occultRecords": true,
    "orchestrionRolls": true,
    "questSequences": true,
    "quests": true,
    "tamedBeasts": true,
    "tripleTriadCards": true,
    "tripleTriadNpcs": true
  },
  // optional — why a false category is off, when it is worth saying
  "categoryNotes": {
    "orchestrionRolls": "In testing — it will switch on once it is ready."
  },
  "crucibleRuns": {            // the Crucible run sharing's switches (see its endpoint below)
    "enabled": false,          // its kill switch (global/per-user/category/flag folded in)
    "heartbeatSeconds": 60,    // idle upload cadence while inside a board
    "note": "In testing — …"   // optional: present only when the flag alone switched it off
  },
  "enabled": true,             // global kill switch
  "intervals": {
    "fullSyncMinutes": 30,     // full-sweep upload cadence
    "unlockDebounceSeconds": 5 // debounce after an Unlock event before uploading
  },
  "itemManifest": [7851, 7852], // the flat manifest: proof item IDs, kept for clients without group support
  "itemManifestGroups": [       // named consent groups; when present, these define what may be scanned
    {"key": "relic-proofs", "label": "Relic weapons, tools & armor", "ids": [7851, 7852], "legacy": true},
    {"key": "relic-materials", "label": "Relic materials", "ids": [5106]},
    {"key": "relic-currencies", "label": "Currencies (including gil)", "ids": [1, 28]}
  ],
  "itemOmitWhenUnseenIds": [45043, 45044], // content-bound ids: omit from uploads when no source saw them
  "manifestVersion": "a1b2c3d4e5f6",
  "occultTracker": {           // the live occult tracker's switches (see its endpoint below)
    "enabled": true,           // the tracker's kill switch (global/per-user/category folded in)
    "heartbeatSeconds": 60     // idle re-upload cadence while inside an instance
  },
  "questSequenceManifest": [70991] // quests whose journal sequence byte to report
}
```

- **Kill switches.** `enabled` is the global switch; `categories` is per-category. **The
  client must honor both**: stop uploading entirely when `enabled` is false, and skip
  collecting/sending disabled categories. The server enforces them too, but a compliant
  client saves the round trips. A gated category ships as an explicit `false`, never omitted —
  an absent key means "the server has never heard of it", which the client reads as enabled.
  **`glamour` and `appearance` are the exception:** they are worth reading and sending only to a
  server that knows them, so for these two an absent key — or no `/config` yet — reads as
  **off**, and the plugin neither collects nor sends them. The plugin's settings show "Not
  offered by <host>." (the configured website's address) once a `/config` leaves them unnamed,
  and "Waiting for <host> to say whether it offers this." before any `/config` has arrived. A
  server that implements them sends either as `false` with a `categoryNotes` sentence while the
  feature is closed to that user, which the plugin shows instead.
- **Category notes.** `categoryNotes` is optional, keyed like `categories`, and explains why a
  category is off. It is absent entirely when nothing needs explaining, never an empty map.
  **Presence is the whole signal** — the client prints the sentence verbatim and never parses
  it, so either side can reword without a release. The two reasons a category is false are not
  equal, and only one carries a note:

  | Why the category is false | `categories` | `categoryNotes` | What the client shows |
  | --- | --- | --- | --- |
  | Ops kill switch (off for everyone) | `false` | *absent* | its own generic line |
  | Feature flag this account cannot see | `false` | present | the note |
  | Both at once | `false` | *absent* | its own generic line |

  The kill switch is deliberately the louder signal: during an outage a specific explanation
  would be a guess. A note against an **enabled** category is legal and ignored. A note value is
  a **non-empty string** — never `null`, and a category with nothing to say is left out of the map
  rather than mapped to `null` or `""`. Treat the text as untrusted like every other server string
  — the backend is user-overridable, so a client must survive a peer that breaks any of the above.
  The plugin folds a note to a single line and clamps it to **500 characters**, marking a shortened
  one with an ellipsis, so copy written longer than that will be cut. It also drops invisible
  formatting that could misrepresent the sentence — bidirectional controls and zero-width spaces —
  while keeping the zero-width joiner and non-joiner, so emoji sequences and Persian or Arabic
  spellings survive intact.
- **Item manifest.** The item IDs the server wants possession counts for. The plugin checks
  possession of **only** these items. When `itemManifestGroups` is present it takes
  precedence; the flat list stays in the config permanently for clients without group
  support, and serves proof ids only.
- **Item manifest groups.** Named consent groups splitting the manifest: `key` is a stable
  consent identifier (a rename is a NEW group and re-prompts consent); `label` is
  user-facing; `legacy: true` marks a group whose scope pre-group items consent already
  covered — the plugin's one-time migration auto-enables exactly those. Everything else
  defaults OFF until the user opts in per group. The plugin scans the union of the enabled
  groups, deduplicated in first-seen order (an id may legitimately appear in more than one
  group). A config with no groups field — or an empty array — falls back to the flat
  `itemManifest`.
- **`itemOmitWhenUnseenIds`** (optional). Manifest ids whose entry the plugin must **omit
  from the upload when no scan source resolved a value**, instead of reporting the explicit
  `count: 0`. These are the content-bound currencies (Occult Crescent's pieces, for
  example): the game only exposes their counts while the character is inside that content,
  so out-of-zone their absence means "not visible from here", never "owns none" — an
  explicit zero would clobber the real count the server holds. A value resolved by any
  source is sent normally. Always a subset of the served count-group ids; a config without
  the field omits nothing. In practice the plugin's readers only record nonzero counts, so
  a genuine in-zone zero balance also reaches the server as an omission — equivalent under
  the server's apply-time backstop, which drops all-zero entries for these ids anyway (that
  backstop also covers older clients that still send explicit zeros).
- **`manifestVersion`.** A content hash, changing whenever the served manifest content —
  the groups and the omit-when-unseen set — changes, so the plugin can skip re-scanning
  inventory when the version it last scanned against is unchanged. Echo it back in the
  sync payload's optional `manifestVersion` field. Compare for equality only — it is a
  hash, not a counter.
- **`questSequenceManifest`** (optional). The quest ids whose journal sequence the plugin
  should report through the `questSequences` sync category. The server names only quests
  with several sequential turn-ins, where the sequence byte is the sole client-side trace
  of mid-chain progress. The plugin looks up **only** these ids and never interprets the
  bytes. Deliberately outside the `manifestVersion` hash — the lookup is a handful of
  in-memory reads, nothing to cache-skip. A config without the field asks about nothing
  (the category is skipped, not sent empty); the `questSequences` category rides the
  standard `categories` kill-switch map.
- **`crucibleRuns`** (optional). The Crucible run sharing's switches. A config without the
  block means the server has no Crucible endpoint. `enabled` folds the global, per-user and
  category switches and the feature flag into one value, and the endpoint enforces it too.
  `note` follows the `categoryNotes` rules: present only when the flag alone switched the
  feature off, never on a kill switch.

Statuses: **200**, **401**, **405** (non-GET).

### POST /api/plugin/v1/sync

A full or incremental collection snapshot for one character, applied monotonically.

#### Request

`Content-Type: application/json`, and a **`Content-Length` header is required** — a chunked
request without it is rejected with **413**. Maximum body size is **1 MiB** by default.

```jsonc
{
  "characterContentIdHash": "…64 lowercase hex chars…",
  "characterName": "Some Name", // first-upload binding + friendly 403s only
  "homeWorld": "Excalibur",
  "pluginVersion": "1.0.0",
  "manifestVersion": "a1b2c3d4e5f6", // optional — the /config value the items list was built from
  "trigger": "login", // "interval" | "login" | "manual" | "unlock"
  "collections": {
    // EVERY key optional — send what was readable
    "achievements": [1, 2],
    "minions": [2, 8],
    "mounts": [1, 5],
    "quests": [65575, 66216], // Quest Excel row ids == the server's Quest.id
    "items": [{"id": 7851, "count": 1, "hqCount": 2, "fresh": true}],
    "questSequences": {"70991": 3}, // active journal sequence byte per manifested quest
    "occultProgression": { // phantom jobs keyed by MKDSupportJob row id (0 = Freelancer, real)
      "jobs": {"0": {"exp": 1200, "level": 4}},
      "knowledge": {"level": 40, "observedAt": "2026-08-12T20:00:00Z"} // optional sighting
    },
    "occultRecords": [1, 2, 55], // MKDLore row ids — the complete SeenLore list
    "orchestrionRolls": [1, 113, 580], // Orchestrion sheet row ids — the tunes, not the roll items
    "tamedBeasts": [{"number": 30}], // bestiary numbers == XBMPet row ids; rank/battlehorn optional
    "tripleTriadCards": [1, 475], // TripleTriadCard sheet row ids
    "tripleTriadNpcs": [2293762], // TripleTriadResident row ids (== TripleTriad row ids)
    "glamour": { // current holdings, not history; an unread container's list is omitted
      "version": 1,
      "dresser": [{"id": 2642, "hq": true, "stains": [12, 0]}], // one entry per loose dresser slot
      "outfitGlamours": [{"outfitId": 45094, "pieceIds": [2642, 2965]}], // one per outfit slot
      "armoire": [3747],
      "held": [ // gear outside the dresser and Armoire: one entry per item per place (and retainer)
        {"id": 2965, "place": "bags", "count": 1},
        {"id": 3747, "place": "retainer", "retainer": 1, "count": 2},
        {"id": 3747, "place": "market", "retainer": 1, "count": 1}
      ],
      "retainers": [{"key": 1, "id": "<64 lowercase hex>", "name": "Mogwin", "market": true}]
    },
    "appearance": { // one record about the local character
      "version": 1,
      "customize": {"Race": 1, "Gender": 1, "ModelType": 1, "Height": 50, "Tribe": 2 /* …21 more */},
      "glasses": [0, 0],
      "fcCrest": {"head": false, "body": true, "offHand": false},
      "weaponHidden": false, "hatHidden": false, "visorToggled": false, "vieraEarsHidden": false
    }
  },
  "collectionScopes": { // optional — per-category completeness; omitted key/object == "partial"
    "orchestrionRolls": "full", // "full" | "partial"
    "tripleTriadCards": "full"
  },
  "itemSources": { // optional — how each storage source was read this pass
    "inventory": {"state": "live"},
    "currencies": {"state": "live"},
    "saddlebag": {"state": "cached"},
    "retainers": {"state": "cached", "count": 3, "total": 5},
    "armoire": {"state": "loaded"},
    "glamourDresser": {"state": "unscanned"}
  }
}
```

Field constraints:

| Field                    | Constraints                                                                              |
| ------------------------ | ---------------------------------------------------------------------------------------- |
| `characterContentIdHash` | matches `^[0-9a-f]{64}$` (lowercase hex SHA-256)                                          |
| `characterName`          | trimmed, 1–100 chars                                                                      |
| `homeWorld`              | 1–100 chars                                                                               |
| `pluginVersion`          | 1–50 chars                                                                                |
| `manifestVersion`        | optional, ≤ 100 chars                                                                     |
| `trigger`                | `interval` \| `login` \| `manual` \| `unlock`                                            |
| id-list categories       | arrays of positive integers, **max 50,000 ids per category**                             |
| `items`                  | `{id: positive int, count: non-negative int, hqCount?: non-negative int, collectableCount?: non-negative int, fresh: boolean}[]`, **max 10,000 entries** |
| `itemSources`            | optional object keyed by source name, always sent with `glamour`; each value `{state: "live"\|"cached"\|"unscanned"\|"loaded", count?: int, total?: int}` |
| `questSequences`         | object mapping quest id (digit-string key, ≤ 10 digits) → sequence byte (int 0–255), **max 100 entries** |
| `occultProgression`      | `{jobs, knowledge?}` — `jobs` maps job id (digit-string key, ≤ 3 digits, no leading zeros) → `{exp: int 0–100M, level: int 0–255}`, **max 64 entries**; `knowledge` is `{level: int 0–255, observedAt: ISO 8601 UTC with a trailing Z (numeric-offset forms are a 400)}` |
| `tamedBeasts`            | `{number: positive int, rank?: int 1–25, battlehorn?: int 1–3}[]`; unknown numbers are dropped with a warning, `0` fails validation — see the id-space bullet for what the deployed server enforces |
| `glamour`                | `{version: 1, dresser?, outfitGlamours?, armoire?, held, retainers?}` — `dresser` `{id, hq?: true, stains?: [int, int]}[]`, **max 1,000**; `outfitGlamours` `{outfitId, pieceIds: int[]}[]`, **max 1,000**; `armoire` id array, **max 5,000**; `held` `{id, place, retainer?, count: int ≤ 9,999}[]`, **max 20,000**; `retainers` `{key: int 1–10, id: 64 lowercase hex, name?: string ≤ 32, market?: true}[]`, **max 10**, keys and ids unique, at most one `market`. Past any limit, or with any other violation (an unknown `place`, a `retainer` key not in `retainers`), this key alone is dropped and named in `rejectedCategories`; the rest of the upload applies — see its bullet for what the deployed server enforces |
| `appearance`             | `{version: 1, customize, glasses, fcCrest, weaponHidden, hatHidden, visorToggled, vieraEarsHidden}` — `customize` maps each of 26 fixed names to a raw byte (int 0–255); `glasses` two ints; `fcCrest` `{head, body, offHand}` booleans; the four toggles booleans. Any violation drops this key alone and names it in `rejectedCategories`; the rest of the upload applies — see its bullet for what the deployed server enforces |
| `collectionScopes`       | optional object keyed by category name, each `"full"` \| `"partial"` exactly (anything else is a 400); omitted key or object == `"partial"` |

- **Unknown `collections` keys are stripped and logged, never rejected** — a plugin newer
  than the server keeps working (payload evolution is additive-only). An older plugin simply
  omits keys, which is safe under monotonic writes.
- **A bad `glamour` or `appearance` drops that key alone.** When either object fails validation —
  the wrong shape, a list past its cap, a `version` the server does not accept — the server drops
  that one key, applies the rest of the upload, and names the dropped key in the 200 response's
  `rejectedCategories`. Envelope errors and bad id-list categories still fail the upload as a
  whole, with a **400** that applies nothing. Both objects are versioned, and the server accepts
  `version: 1` only. Inside either object a field the server does not name is dropped silently
  rather than rejected, so a new field ships with a new `version`; under the same number the
  server would discard it without a word.
- **Ids the server's catalog does not recognize are ignored, never an error.** The
  plugin is a dumb fact-reader and should send every id the game reports; the server's
  catalog tables are deliberately pruned subsets (quests especially) and can trail the
  game after a patch. Each category's ids are filtered against its catalog before
  writing: unknown ids are dropped (and logged server-side), the known ids in the same
  payload still write, and the upload succeeds. Nothing is lost — the plugin re-sends
  everything on every sweep, so a dropped id lands as soon as the catalog imports it.
  Dropped ids are simply absent from the `written` counts.
- An **empty array carries no facts** and writes nothing (absence and emptiness are both "no
  information"). Inside `glamour` the two differ: an empty list there reports a container read
  and found empty (see its bullet).
- **Explicit zeros.** An `items` entry PRESENT — even with `count: 0` — is a reported fact
  for that id; an id ABSENT from the list was not scanned and carries no information. What
  a count *means* is decided per id by which manifest group the id belongs to — see the
  proof vs. count-tracked split under [Behavior](#behavior-the-plugin-author-should-know).
  Uploads are filtered to the served manifest at apply time, so stale-manifest or
  out-of-catalog ids are dropped before writing. The one exception: ids the config lists
  in `itemOmitWhenUnseenIds` are OMITTED when no source resolved them (see the `/config`
  section) — for a content-bound currency, "absent" is the honest report of "not visible
  from here".
- **Per-quality counts.** `count` is normal-quality copies only; optional `hqCount` and
  `collectableCount` are omitted when zero. The plugin never sums qualities; whether HQ
  satisfies a requirement is the server's policy.
- **`itemSources`** tells the server which storage sources contributed to the counts (a
  zero while retainers are unscanned is a floor, not truth) and powers "open your saddlebag
  once" hints. The retainer entry's `count` is how many retainers the cache remembers; the
  optional `total` is how many the character has, when the game can say — `3` of `5`
  scanned means two retainers contribute nothing yet. Both are counts only; nothing
  identifies an individual retainer. `inventory` covers the containers read live each pass
  (bags, equipped gear, the armory chest, crystals); `currencies` covers the game's
  currency subsystem (gil, tomestones, scrips, and the rest), also read live. `itemSources`
  also accompanies every `glamour` upload, with the same keys and states (every source but
  `currencies`, which only `items` reports), whether or not `items` is switched on: it is how
  the server decides which containers the snapshot read. One entry per source serves both
  categories, because both gate their reads and build these entries from the same code. For
  `glamour` the state also decides whether a missing piece is evidence, per place (see its
  **Current-ness per place** bullet). A source counts as **current** when it is `inventory`
  `live`; `armoire` `loaded`; `glamourDresser` or `saddlebag` `cached` (the game refreshes those
  two copies when their window closes, a copy counts as `cached` only once that has happened this
  session, and the glamour category never reads while one is open); or `retainers` `cached` with
  `count` equal to `total`. `unscanned` never counts. A character that has not unlocked its
  chocobo companion has no saddlebag, so `saddlebag` is `cached` with nothing from it: an empty
  source, current without a read. The plugin decides "has none" only once the player's state has
  loaded. A character with no retainers stays `unscanned`, because the game offers no signal that
  separates "has none" from "not loaded yet". The accepted source keys are a **closed set**
  (`inventory`, `saddlebag`, `retainers`, `armoire`, `glamourDresser`, `currencies`) — an
  unrecognized key fails validation and rejects the whole upload, so a new source key ships
  server-first, and any source the plugin tracks for display only (an unreadable source such as
  mannequins) must stay off the wire.
- `fresh: false` means the count came from a cache rather than a live container read. The
  server treats a stale positive as a positive (the item *was* there), so the flag does not
  change the outcome.
- **Orchestrion id space.** `orchestrionRolls` carries `Orchestrion` sheet row ids — the tunes
  themselves, the same ids the game's unlock state answers for. Do **not** send the
  `"… Orchestrion Roll"` **item** ids; those live in the `Item` sheet, the server stores
  them separately, and it silently drops them here as unknown. The sheet is **sparse**: the
  server's catalog holds 883 rolls in the range 1–891, the gaps being rows it skips (rows
  with no English name, for instance). Row 0 is a dummy and must never be sent — every
  id-list category is validated as strictly positive, so a single `0` rejects the **whole
  upload**, unlike an unknown id. Unlock state is readable anywhere, in or out of content,
  so a full sheet sweep is always possible; see `collectionScopes` below for what declaring
  that sweep complete buys.
- **Mount and minion id spaces.** `mounts` carries `Mount` sheet row ids and `minions` carries
  `Companion` row ids, both as the sheet reports them. Unlock state resolves through a bitmask
  slot, so a row with no slot always answers "not unlocked" — the same shape as the Triple Triad
  gap below, and worth stating because there it hides real opponents. The two collections reach
  that bitmask differently, so only one of them has the gap at all.
  **Minions** have none: the client sizes its minion bitmask to `Companion`'s row count and
  indexes it by row id, so every row has a bit by construction and nothing is unreportable.
  (`Companion.Order` is a display ordering and says nothing about unlock state.)
  **Mounts** do have slots — the mount bitmask is sized by the largest `Mount.Order`, so a row
  with a negative `Order` has no bit — but nothing a player can own falls in the gap. Measured
  against the 7.56 sheets, `Mount` has 451 rows of which 78 are slotless and only 13 of those
  carry a name — every one a cutscene camera
  (`CAM-I`, `CAM-II`, `CPD-I`), an internal designation (`CHL P-0005` and its siblings) or a
  duty vehicle (`commandeered magitek armor`, `marid`, `true griffin`,
  `xenoscaping vacuum suit`, `Red Baron`), none of them present in the game's own Mount Guide —
  which is what settles it, since a row nobody can obtain answers "not unlocked" whichever field
  indexes the bitmask.
  Both categories may therefore declare themselves complete (see `collectionScopes` below).
  Re-check the mount side after a patch that adds mounts, with `/shinies dumpslots` on a Debug
  build: a real collectable landing in a slotless row would silently falsify that claim. The
  minion side needs no re-check for slots, only that the client keeps sizing the bitmask to the
  sheet's row count.
- **Triple Triad id spaces.** `tripleTriadCards` carries `TripleTriadCard` sheet row ids
  (1–475, dense; row 0 is a dummy). `tripleTriadNpcs` carries `TripleTriadResident` row ids
  **exactly as the sheet reports them** — they live in the game's event-handler id range
  (2293762 and up) and must not be rebased; the server stores them as `TripleTriad` row
  ids, the same key space. Sheet rows whose `Order` is 65535 have no beaten flag and are
  never sent — but note they are **not** all placeholders: Lewena (`2293811`) is a real,
  challengeable opponent the game simply does not track, because she counts toward no
  Triple Triad achievement. The server's catalog carries such opponents, so a player can
  own one the plugin cannot report. That is why `tripleTriadNpcs` never declares itself a
  complete list (see `collectionScopes` below). Do **not** send `ENpcResident` ids — they
  are a different sheet in a different range, and the server silently drops them as unknown.
- **`questSequences`** carries an entry only for a manifested quest **currently in the
  journal**: the key is the quest's Excel row id as a decimal string, the value the raw
  sequence byte the game reports. An empty object means "every manifested quest was
  checked; none is active"; omitting the category means it was not read. The bytes are
  opaque, game-defined values per quest — the plugin reports them uninterpreted, and the
  server's curated tables decide what each byte proves. Observations are **sticky
  server-side**: a quest absent from a later upload (abandoned, completed, never started —
  the plugin cannot tell which) never clears previously derived credit.
- **Occult id spaces & semantics.** `occultProgression.jobs` is keyed by `MKDSupportJob`
  row ids (0–23, and **0 — Freelancer — is a real job**); values come from the occult
  instance director, which the plugin can read only inside an Occult instance. `jobs` may be
  sent as an **empty map** — the server accepts it and an empty map writes nothing — which is
  how a knowledge sighting taken outside an instance still reaches the server. Job writes
  are monotonic by (level, exp): a stale pair writes nothing. `occultProgression.knowledge` is
  the TRUE knowledge level from the review window (the in-instance HUD shows only the
  zone-synced level), sent with the time the window was opened; the server keeps the
  **freshest** observation across plugin and Lodestone sources, never a maximum — death
  without a raise can de-level knowledge. `occultRecords` carries `MKDLore` row ids — the
  game's `SeenLore` list IS the character's complete seen-set, readable anywhere, so the
  category declares itself `"full"` (see the `collectionScopes` bullet below). Storage is
  sticky insert-only, and uncataloged ids drop under the catalog-trailing rule.
- **`tamedBeasts` id space & semantics.** ⚠️ *The deployed server does not implement this category:
  its key is stripped on arrival under the unknown-key rule, so none of the validation described
  here is enforced, and this describes what the plugin sends rather than what the server does. The
  deployed server wins over this doc, as always.* Entries are objects, not bare ids: `{"number": n}`,
  where `n` is the bestiary number — the `XBMPet` sheet row id (1–50), which is what the
  server's catalog is keyed on. `rank` (1–25) and `battlehorn` (1–3) are accepted alongside
  `number` and are omitted while the plugin has no way to read them; the object shape is what
  lets them appear later without the category changing shape. Numbers outside the catalog are
  dropped with a warning rather than a 400, but `0` fails validation like every id-list
  category. **Completeness is decided per upload here as well as per category**, because the
  bestiary only becomes readable when the player opens it. The plugin learns it from the Master's
  Bestiary window, which shows part of itself at a time and carries its own "held out of total"
  tally; the category declares `"full"` only once three things agree — the game's own bestiary
  size matches the window's total, every one of those numbers has been seen, and the beasts the
  pages reported as held match the largest held count the window has reported this login session.
  Both of the window's figures are kept at their session maximum rather than taken from the latest
  page: pacts are never broken, so a smaller held count can only be a narrower view of the same
  beasts, and a size that ever came back short — a list still loading — must not revoke a claim
  already earned. The second agreement is what stops a filtered view from claiming the whole:
  filtering narrows the tally's held count but leaves the bestiary's real size beside it, so a
  filtered window can shrink one half of its own check — and it still cannot list the numbers it is
  hiding, which is what the second agreement counts. The first keeps that honest by refusing a
  window whose total disagrees with the game's own data at all. Until all three agree it is
  `"partial"` and an absent number means only "not seen". The server writes `plugin_acquired`
  monotonically, insert-only, stamping the upload time on first sight.
- **`glamour` semantics — current holdings.** ⚠️ *The deployed server does not implement this
  category. Its `/config` does not name it, so the plugin neither reads nor sends it (a key that
  did arrive would be stripped under the unknown-key rule), and none of the validation or clearing
  described here happens on that server; this describes what the plugin sends to a server that
  names it. The deployed server wins over this doc, as always.* Unlike every other
  category, `glamour` is what the character holds **now**, not a record that only grows: for a
  place read as current (see **Current-ness per place** below), a piece missing from it has left
  it. The server tracks storage per container, clearing a piece from a container that was read and
  no longer holds it. The server's ownership flag for a piece is slower to clear. An **all-current
  miss** is an upload whose sources were all current and whose whole snapshot lacks the piece;
  the flag clears only on a second all-current miss at least 25 minutes after the first, and a
  miss inside that gap writes nothing. A manual mark is never cleared; it shows as disputed. So
  absent and empty are different facts: a list is **omitted** when its container was not read
  (`dresser` and `outfitGlamours` while `glamourDresser` is unscanned, `armoire` while `armoire`
  is unscanned) and `[]` when it was read and holds nothing. `version` and `held` are always
  present.
  - `dresser` has one entry per loose dresser slot, so two copies are two entries. `id` is the base
    item id; `hq: true` marks a high-quality copy, and an absent `hq` means normal quality. `stains`
    holds the two dye channels (0 = undyed) only when they were readable this pass — the game keeps
    them only in the dresser's live copy, which is loaded in the zone where the dresser was opened —
    so an absent `stains` means unknown, never undyed. An outfit slot never appears here.
  - `outfitGlamours` has one entry per outfit slot. `outfitId` is the `MirageStoreSetItem` row id,
    which is the outfit item's id, and repeats when two copies are stored. `pieceIds` lists the
    pieces currently stored in it, sorted and distinct, and may be `[]`. No stains or quality:
    storing an outfit removes its dyes.
  - `armoire` lists the item ids the Armoire holds, readable only once the player has opened it
    that session.
  - `held` has one entry per base id per place, and per retainer for a `retainer` or `market`
    place, its `count` the copies that place holds at every quality. `place` is one of `bags`,
    `armory`, `equipped`, `saddlebag`, `retainer` (a retainer's bags and equipped gear) and
    `market` (one retainer's listings, normally the one summoned most recently; listings on other
    retainers are not visible). `retainer` is present exactly on a `retainer` or `market` entry
    and names a `key` in `retainers`. Glamour gear only — an item with an equip slot, soul
    crystals excluded — and never the dresser or the Armoire, which have their own lists.
  - `retainers` lists every retainer whose saved copy was read this pass, and only those; it is
    omitted when none was. `key` (1–10, unique in the upload) is what `held` entries name; `id` is
    the SHA-256 digest of the retainer's id, the same representation as the character's
    (`characterContentIdHash`), so the raw id never travels; `name` (at most 32 characters) is
    present whenever the game has loaded its retainer list this session, unless it is blank or
    longer, and the server keeps the last name it received. `market: true` sits on the one retainer
    whose listings were read this pass, even when it lists no gear, so "listed nothing" differs
    from "not read". The plugin credits listings to the retainer the game says was selected last,
    and only when the number of listings read matches that retainer's own listing count; listings
    it cannot credit, or whose retainer's own copy was not read, are not sent.
  - **Current-ness per place.** `bags`, `armory` and `equipped` are current when `inventory` is
    `live`; `saddlebag` when `saddlebag` is `cached`; a retainer's `retainer` place when that
    retainer is listed in `retainers`; its `market` place when it carries `market: true`. Each
    current place is brought in line on its own. Clearing the ownership flag, by contrast, needs
    every source current, `retainers` `cached` with `count` equal to `total` included.
  - The plugin skips the category (absent, so nothing clears) while a storage window is open (the
    Glamour Dresser and its outfit-glamour window, the Armoire, the saddlebag) or a summoning bell
    is in use; when the inventory cannot be read — no inventory is available,
    bags, equipped gear or an armory chest are not loaded, or a remembered retainer cannot be read;
    when a dresser reading cannot be interpreted; and when a game sheet it needs cannot be read. A
    list, a held count or the number of retainers past its limit withholds the whole category too,
    never truncated or clamped, since a shortened list would read as pieces removed. It is sent on
    full sweeps only (`login`, `interval`, `manual`), never on an `unlock` upload, and carries no
    `collectionScopes` declaration.
- **`appearance` semantics.** ⚠️ *The deployed server does not implement this category. Its
  `/config` does not name it, so the plugin neither reads nor sends it (a key that did arrive would
  be stripped under the unknown-key rule), and none of the validation described here happens on
  that server; this describes what the plugin sends to a server that names it. The deployed server
  wins over this doc, as always.* One record describing how the local character looks, sent
  whole each time. It is versioned, and a layout change ships as a new `version`; the server
  accepts `version: 1` only (see the bad-key bullet above). `customize` always carries all 26 keys,
  named and ordered exactly as the
  game's customization bytes: `Race`, `Gender`, `ModelType`, `Height`, `Tribe`, `FaceType`,
  `HairStyle`, `HasHighlights`, `SkinColor`, `EyeColor`, `HairColor`, `HairColor2`, `FaceFeatures`,
  `FaceFeaturesColor`, `Eyebrows`, `EyeColor2`, `EyeShape`, `NoseShape`, `JawShape`, `LipStyle`,
  `LipColor`, `RaceFeatureSize`, `RaceFeatureType`, `BustSize`, `Facepaint`, `FacepaintColor`. Each
  value is the raw byte — a color byte is a palette index, not a color. Five bytes are packed,
  carrying a flag in bit 7 (`0x80`): byte 7 `HasHighlights` (highlights on), byte 12
  `FaceFeatures` (bits 0–6 switch facial features 1–7 on, bit 7 is the legacy tattoo), byte 16
  `EyeShape` (small iris), byte 19 `LipStyle` (lipstick) and byte 24 `Facepaint` (reversed). They
  travel raw, and the server stores `customize` exactly as sent, as the 26 named raw bytes: it
  unpacks nothing, and the site's fitting room unpacks the packed bytes when it reads them.
  `glasses` is always two `Glasses` sheet row ids; the second slot is unused in practice and
  arrives as 0. `fcCrest` says per slot whether the free company crest is shown, and the four
  toggles are reported as shown in game. Read from the local player only; the plugin skips the category when no
  character is loaded, while the character is transformed (drawn as something other than
  themselves), and when the read's size disagrees with the plugin's layout table. Like `glamour`,
  it is sent on full sweeps only, never on an `unlock` upload.

#### Response (200)

```jsonc
{
  "ok": true,
  "bound": false, // true only when THIS request performed the first-upload bind
  "written": {
    // rows created + promoted per id-list category — always every key the server tracks,
    // whether or not the upload carried that category
    "achievements": 0,
    "minions": 2,
    "mounts": 1,
    "quests": 12,
    "tripleTriadCards": 5,
    "tripleTriadNpcs": 1
  },
  "achievementsSkipped": "not_sent", // present iff the achievements key was absent or stripped as disabled (an explicit empty array is "sent")
  "provenSteps": 3, // present iff items were applied and relic-proof derivation succeeded
  "itemCounts": 1268, // rows written to item-count storage by this upload's items
  "skippedCategories": ["minions"], // present iff the server stripped disabled categories from this payload
  "rejectedCategories": ["appearance"], // present iff a glamour or appearance key failed validation and was dropped alone; never overlaps skippedCategories
  "storedSequences": 1, // present iff questSequences survived the strip and stored; NEW observations this upload (0 = all already known)
  "storedProgression": 3, // present iff occultProgression survived and stored; jobs whose value ADVANCED (knowledge not counted)
  "storedRecords": 5, // present iff occultRecords survived and stored; NEW sticky rows this upload
  "storedGlamour": 412, // present iff glamour survived and stored; known pieces the snapshot holds
  "storedAppearance": true // present iff appearance survived and was processed; false = not applied
}
```

Optional keys are **omitted rather than null**, so the plugin can feature-detect them. The
example lists every optional key at once for reference; a real response never names a category
in `skippedCategories` or `rejectedCategories` and also carries that category's `stored…` key.
`items` never appears in `written` (it feeds relic proofs and count storage, not a
collection count). The plugin reads `written` as a plain category-keyed map, so a category
it has never heard of arrives intact and a server that names fewer causes no error.

- **`rejectedCategories`** names each key dropped under the bad-key rule above (only `glamour`
  and `appearance` can appear); it is omitted when nothing was rejected, never an empty array,
  and never names a key that `skippedCategories` names.
- **`storedGlamour`** is the number of known pieces the stored snapshot holds. It is absent when
  the category was skipped, rejected or failed.
- **`storedAppearance`** is `true` when the record was applied and `false` when it was not: the
  owner has pinned a look on the site, or a newer report already landed. It is absent when the
  category was skipped, rejected or failed.
- These three keys, like the two categories, come only from a server that implements them (see
  the ⚠️ notes on `glamour` and `appearance` above).

`itemCounts`, `storedSequences`, `storedProgression`, `storedRecords`, `storedGlamour` and
`storedAppearance` are informational, like `written`: the plugin ignores them — no plugin
logic may branch on them.

#### Status codes

| Status  | Body                                                            | Plugin behavior                                                                                                       |
| ------- | --------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| **200** | see above                                                       | Applied.                                                                                                             |
| **400** | `{"error": "invalid_payload", "issues": {…}}`                   | Validation failed; `issues` is `{fieldErrors, formErrors}`. A non-JSON body gets the same shape with a `formErrors` message. A bad `glamour` or `appearance` alone is not a 400 (see `rejectedCategories`). Don't retry unchanged. |
| **401** | `{"error": "invalid_token"}` + `WWW-Authenticate: Bearer`       | Token missing/malformed/unknown. Stop; user must generate a new token.                                               |
| **403** | `{"error": "<code>", "name": "…", "world": "…"}`               | Character resolution failed; `<code>` says why (see [403 recovery](#character-binding)). Render the fix for that code. Don't retry until the user acts. |
| **405** | —                                                               | Wrong method (the route accepts only POST).                                                                          |
| **413** | `{"error": "payload_too_large"}`                                | `Content-Length` missing, non-numeric, or over the cap. Don't retry unchanged; split the upload.                    |
| **429** | `{"error": "rate_limited"}` + `Retry-After: <s>`                | Over the per-token limit. Sleep at least `Retry-After` seconds (whole seconds, rounded up).                          |
| **500** | —                                                               | The transactional apply failed. Safe to retry later — writes are idempotent.                                        |
| **503** | `{"error": "sync_disabled"}` + `Retry-After: 3600`              | Global kill switch is off. Back off the full hour.                                                                   |

### POST /api/plugin/v1/occult/instance-state

The live Occult Crescent tracker upload: one compact **full snapshot** of the instance the
character is standing in — every CE/tower container slot plus tracked FATEs — sent on
status change (debounced; never progress ticks), as a heartbeat every
`occultTracker.heartbeatSeconds` from `/config`, and on enter/leave. Deliberately NOT the
batch `/sync` endpoint: small, frequent, instance-scoped. A server whose `/config` carries
no `occultTracker` block does not have this endpoint; the client must stay silent then.

#### Request

`Content-Type: application/json`; `Content-Length` required (same gate as `/sync`). A real
snapshot is ~1.2 KB.

```jsonc
{
  "characterContentIdHash": "…64 lowercase hex chars…",
  "characterName": "Some Name", // same binding identity as /sync
  "homeWorld": "Excalibur",
  "pluginVersion": "1.0.0",
  "trigger": "change",          // "change" | "enter" | "heartbeat" | "leave"
  "instance": {
    "territoryTypeId": 1252,    // 1252 South Horn | 1346 North Horn
    "worldId": 73               // OPTIONAL: the reporter's CURRENT World row id (not home
                                // world). The server maps world → data center to scope
                                // matching and the browse list per DC; omitted when
                                // unreadable, and the tracker then stays un-scoped. An
                                // unknown id is ignored (catalog-trailing rule).
  },
  "encounters": [
    // Full state every upload. CEs and the Forked Tower by DynamicEvent row id;
    // FATEs by Fate sheet row id. status is the THREE-word vocabulary only:
    // "preparing" (CE Register/Warmup) | "active" (Battle / FATE on the table)
    // | "down" (Inactive / removed).
    {"dynamicEventId": 43, "status": "active", "sinceUtc": "2026-08-11T16:02:15Z"},
    {"dynamicEventId": 48, "status": "down", "sinceUtc": null}, // the tower rides along
    {"fateId": 1972, "status": "active", "sinceUtc": "2026-08-11T15:46:48Z"}
  ]
}
```

- **`sinceUtc` is the fingerprint.** Occult instances have no client-readable id; the
  server matches an upload to the active tracker (same territory) sharing at least one
  exact `(encounter, sinceUtc)` pair, bridging quiet gaps with the reporter's presence.
  So the plugin must send **server-assigned epochs, identical for every observer** — a
  FATE's start epoch, a CE phase deadline (or battle start derived as deadline −
  duration) — at exact whole-second precision, formatted `YYYY-MM-DDThh:mm:ssZ`. The
  plugin's own observation time is the fallback only for transitions the game zeroes
  (the Battle→Inactive flip). `sinceUtc` must be **present** on every row: null where
  the game exposes nothing — null entries carry state but never identity, which is why
  the key is written explicitly rather than omitted.
- **Exactly one id key per row** — `dynamicEventId` or `fateId`, never both; the other
  is omitted. Unknown ids are ignored by the server (catalog-trailing rule); the tower
  ids land on the tracker's tower state rather than an encounter row.
- **`leave`** (sent on territory exit) clears the character's presence only; the tracker
  lives on for reporters still inside. A reporter also ages out after ~3 missed
  heartbeats, so a missed leave self-heals. A leave's `worldId` is sampled at exit, so a
  deferred leave still clears presence on the data center that was actually left.
- **Absence self-heal.** Unlike the monotonic `/sync` writes, this endpoint is a
  replace-style full snapshot: an encounter the tracker holds `active` that is absent
  from an incoming snapshot (except on `enter`, whose table may still be settling) is
  flipped `down` server-side, stamped at receipt. So a client that lost its in-memory state
  mid-visit (a plugin reload, a consent flip) does not strand an encounter as active —
  the next full snapshot from any reporter corrects it.

#### Response

```jsonc
200 {"ok": true, "outcome": "applied", "trackerId": "…uuid…", "created": false}
200 {"ok": true, "outcome": "unresolved", "trackerId": null} // no fingerprintable pair; retry on next change
200 {"ok": true, "outcome": "left", "trackerId": "…uuid or null…"}
```

Status codes mirror `/sync` (400 family, 401, 403 with echoed identity, 405, 413, 429
with its own per-token budget of 240/hour, 503 `sync_disabled`), plus
**503 `{"error": "tracker_unavailable"}`** when the territory's server-side curation is
absent — back off, server-side problem.

### POST /api/plugin/v1/crucible/observations

The Crucible run companion's feed: **snapshots**, each of what one Crucible window (or the
character's own HP) showed at one moment. The server turns them into the companion's run
events; the client never names an event, so the event model can change without a plugin
release. Like the occult tracker it is small, frequent and duty-scoped, and it needs its own
consent toggle (off by default, and outside "Turn on new collections automatically"). A server
whose `/config` carries no `crucibleRuns` block does not have this endpoint; the client must
stay silent then. The server's schema is **strict**: a key it does not name fails the whole
upload with 400 `invalid_payload`, and so does a named key that is missing.

**What the client may read, and must never read.** Only what the game shows the player, and
only while the character is out of combat: board progress, the player's own familiars, the
player's own bag and tokens, treasure, loot and shop offers, the results screen, and the local
character's own HP. Never the battle log or chat, the object table, statuses, damage, or
anything about an enemy (its name, stats, weaknesses or state). A client skips the board
window's enemy rows by their record type, and the results screen's beaten counts are score
figures, not enemy data.

#### Request

`Content-Type: application/json`; `Content-Length` required (same gate as `/sync`). A six-kind
upload with fifteen familiars is about 6 KB; the cap is 64 KB.

```jsonc
{
  "characterContentIdHash": "…64 lowercase hex chars…",
  "characterName": "Some Name", // same binding identity as /sync
  "homeWorld": "Excalibur",
  "pluginVersion": "1.0.0",
  "trigger": "change",          // "enter" | "change" | "heartbeat" | "leave"
  "territoryTypeId": 1339,      // the territory the upload concerns (see below)
  "observations": [             // at most one entry per kind, oldest first
    {"kind": "board", "observedAtUtc": "2026-09-28T12:00:00Z", "closed": false,
     "view": 2, "currentNodeIndex": 4,
     "pieces": [{"nodeIndex": 4, "progress": 0}]},
    {"kind": "team", "observedAtUtc": "…", "closed": true, "mode": 2, "itemId": null,
     "familiars": [{"petId": 10, "place": 0, "hp": {"current": 640, "max": 667},
                    "rank": 5, "rankSynced": true, "feedItemIds": [], "resting": false}]},
    {"kind": "bag", "observedAtUtc": "…", "tokens": 764,
     "itemIds": [78, 130, 138], "gearIds": [2, 45]},
    {"kind": "offer", "observedAtUtc": "…", "closed": false, "source": "shop",
     "tokens": 764, "tokensEarned": null,
     "offers": [{"itemId": 162, "price": 25, "discounted": true, "bought": false}]},
    {"kind": "results", "observedAtUtc": "…", "degree": 0, "rankIndex": 4,
     "score": {"base": 7625, "performancePct": 100, "performancePoints": 5000,
               "enemies": 4, "enemyPoints": 1000, "elites": 1, "elitePoints": 625,
               "bosses": 1, "bossPoints": 1000, "remainingHp": 462,
               "bonusPoints": 250, "total": 7875},
     "bonuses": [{"bonusId": 7, "points": 250}],
     "familiars": [{"petId": 8, "rankBefore": 7, "rankAfter": 8,
                    "expBefore": 65, "expAfter": 62}]},
    {"kind": "self", "observedAtUtc": "…", "hp": {"current": 1180, "max": 1300}}
  ]
}
```

| Kind | Window | Fields |
| --- | --- | --- |
| `board` | `XBMStageDetailList` | `view` 0 (the entrance, before entering), 1 (the whole board), 2 (scoped to the piece being played); `pieces[]` each `nodeIndex` with `progress` 0 not reached, 1 cleared, 2 the branch not taken; `currentNodeIndex` in view 2, else null; `closed`. |
| `team` | `XBMPetParty` | `mode` 0 roster pick, 1 browse, 2 lineup, 3 feed, 4 campsite, 5 Blessed Horn; `itemId` (the feed or horn) in modes 3 and 5, else null; `familiars[]` each `petId` (the `XBMPet` row), `place` 0–2 or 3 for none, `hp`, `rank` as drawn with `rankSynced`, `feedItemIds` (up to five), `resting`; `closed`. Mode 0 lists only the counted picks, in pick order. |
| `bag` | `XBMContentsMainHUD` | `tokens`; `itemIds` (one per filled item slot, so an item in two slots appears twice); `gearIds`. No `closed`: the HUD stays open. |
| `offer` | `XBMContentsTreasure`, `XBMContentsBooty`, `XBMContentsItemShop` | `source` `treasure`, `loot` or `shop`; `tokens`; `tokensEarned` on loot, else null; `offers[]` each `itemId`, plus `price`, `discounted` and `bought` on the shop only; `closed`. |
| `results` | `XBMResult` | `degree` 0–3, or null; `rankIndex` 0 (Apprentice) to 8 (Legendary), or null; `score` as the numbers on screen, where `remainingHp` is the HP the run ended on; `bonuses[]` each `bonusId` (an `XBMScoreBonus` row id, or null) with `points`; `familiars[]` each `petId`, `rankBefore`, `rankAfter`, `expBefore`, `expAfter`. |
| `self` | the local player | `hp`. |

- **Null or absent.** `currentNodeIndex`, `itemId`, `tokensEarned`, `degree`, `rankIndex` and
  `bonusId` are always present, as `null` where they do not apply or did not resolve. The three
  shop fields are absent from a treasure or loot offer, and `closed` from a `bag`, `results` or
  `self`, not null. Every list is present, empty where it holds nothing (`observations: []` on a
  heartbeat or a leave, `feedItemIds: []`).
- **`closed`** marks a window's last snapshot before it closed: a `board`, `team` or `offer`
  snapshot goes up on each refresh that changed it, and once more with `closed: true` when the
  window closes, whether or not its content changed.
- **Snapshots are facts at a time, never deltas.** Every comparison between snapshots is the
  server's.
- **Text the game draws is resolved by the client, never sent raw.** The Degree, the rank and
  each bonus name are drawn in the game client's language; the client resolves them to `degree`,
  `rankIndex` and `bonusId` from the game's own sheets, and sends `null`, never a guess, when a
  string does not resolve.
- **Unknown ids.** An unknown `petId`, item, gear or bonus id is ignored; a `nodeIndex` the board
  lacks fails the whole upload with 400. A view 0 `board` goes up under the entrance, which names
  no board, so the server identifies the board by its piece count, which differs on each of the
  five boards: the snapshot must list every piece, and a `pieces` list whose length and
  `nodeIndex` values fit no board fails the same way.
- **`territoryTypeId`** is the territory the upload concerns, not where the character stands when
  it is sent: the board the character is in, the entrance for the roster pick and pre-entry board,
  on `leave` the board just left, and on `heartbeat` the territory the baseline carried.
- **Outside a board** the client sends nothing except `leave` and, from the entrance (148), its
  roster pick (`team` mode 0) and pre-entry board (`board` view 0), which open before the duty and
  go up as `change`.
- **Refused outright (400 `invalid_payload`, before the character is resolved):**
  - observations under a `territoryTypeId` that is neither a board (1339–1343) nor the entrance
    (148);
  - an entrance window (`team` mode 0, `board` view 0) under any `territoryTypeId` but 148, and
    any other observation under 148;
  - a `heartbeat` or `leave` whose `observations` is not empty;
  - a non-null `currentNodeIndex` outside view 2, a non-null `team.itemId` outside modes 3 and
    5, or a non-null `offer.tokensEarned` outside `loot`.
- **`enter`** (zoning into a board) carries a baseline: `bag` and `self`, plus every window open
  at that moment. The same baseline goes up as a `change` when the client starts or reloads
  inside a board. A kind not yet read when the `enter` goes is left out and follows as a
  `change`: no upload has to carry any particular kind, and a diff starts at a kind's first
  reading, whichever upload carries it.
- **`change`** normally carries only the kinds whose content changed since the client last read
  them. A kind can still repeat unchanged: the comparison starts over with each visit, each login
  and each plugin start, and a reading no request has carried 30 minutes after it was read is
  dropped, so the same content read again goes up. A reading already in a retried request is not
  dropped for its age, so a retry can deliver one older than 30 minutes. Content in an upload
  refused with a 400 is not sent again until it changes or the comparison starts over. A
  window's `closed` snapshot and a fresh snapshot of the same kind go in two uploads, the
  `closed` one first.
- **`heartbeat`** carries `observations: []` and fires only after `crucibleRuns.heartbeatSeconds`
  with no other upload. It reads nothing, so it may go out while the character is in combat.
- **`leave`** (zoning out) carries `observations: []`.
- **Order and retry.** `observedAtUtc` never decreases within an upload (equal stamps are normal,
  since the wire is second-exact). The server checks that order, and refuses a stamp for its
  time only when it is more than five minutes ahead of the server's clock, so a client clock
  running fast still uploads; a stamp may be old. Across uploads the server orders each kind by
  `observedAtUtc`, so a retried upload may arrive late and still land in place, as long as its
  piece's outcome has not been written (see below). Among snapshots that share a stamp, the server
  reads the `board` first, so it starts from the run's position on that `board` when it works out
  which piece the rest of a baseline belongs to. A 429 or 503 is retried after its
  `Retry-After`, and a network failure after a backoff, each with the same content; a 400 or 403
  is not retried unchanged.

#### Response

```jsonc
200 {"ok": true, "outcome": "applied", "runId": "…uuid…", "events": 3, "skipped": 0}
200 {"ok": true, "outcome": "held", "runId": "…uuid or null…"} // not applied to a run (see below)
200 {"ok": true, "outcome": "board_mismatch", "runId": "…uuid…", "boardId": 2}
200 {"ok": true, "outcome": "left", "runId": "…uuid or null…"}
```

In-duty snapshots belong to the character's active run, which the player starts on the website.
On `applied`, `events` is how many run events the server wrote for the upload, which can include
an earlier piece's outcome (see below). `skipped` is how many events the server did not write for
the upload because the player had already logged them on the website; the player's own entry wins.
An undo sticks: a run event the server derived and the player undid is not derived again from a
later snapshot of the same piece and kind.

**What the server makes of the snapshots.** The server's rules for turning snapshots into run
events are its own; these are the ones that shape what a client sends:

- The move onto a fight piece and that fight's lineup are written when the lineup window (`team`
  mode 2) closes, so that window's `closed` snapshot is the one that sets the lineup. If the
  window closes again on the same piece with a different lineup (other familiars placed, another
  order, or different HP) before the fight's outcome is written, the lineup is amended once; any
  later change is the player's to make on the website.
- A piece's outcome (its fight, treasure, shop or campsite) is written when the first snapshot of
  the next piece arrives, or when a `results` snapshot or a `leave` from the active run's board
  arrives. Until then it waits for the `bag` and `team` readings that complete it.
- What the player takes from a treasure or sells at a shop is read from the `bag`: the server
  compares the last `bag` reading before the piece's window opened with the piece's last `bag`
  reading. When they match, or no `bag` arrives after the window opens (a client sends none while
  the bag is unchanged), the server writes nothing taken or sold. What the player buys at a shop
  comes from the `bought` flags on its `closed` shop `offer` snapshot instead.
- A reading that arrives after its piece's outcome was written is not applied, and any correction
  to that piece is the player's to make on the website, so a client sends a retried upload before
  any upload with newer readings.
- When more than one piece ahead could hold it, an `offer` snapshot, or a `team` snapshot in
  lineup, feed or campsite mode (2, 3 or 4), stamped before any `board` that shows where the run
  stands, waits for the next `board` to say which piece it belongs to.
- The finish that `results` brings is written only once the run stands on the board's last piece
  and that piece's fight is written.
- A snapshot identical to one the server already has, stamp included, is applied only once, so
  retrying an upload whose response was lost changes nothing.

For an upload whose `territoryTypeId` is a board, `held` means there is no active run yet: the
server keeps the latest snapshot of each kind for 30 minutes past its `observedAtUtc`, so a
reading that arrives older than that is not kept. The server applies what it keeps once a run on
that board starts. `board_mismatch` means the website run is on a different board: nothing is
written to it, and the snapshots are kept as for `held`, because starting a run on this board
abandons the run on the other one. A `results` snapshot that is not applied to a run is not kept,
and drops every kept snapshot, so a later run never receives an earlier attempt's snapshots. A
`leave` drops every kept snapshot too, and never ends a run, since the player may have suspended
the board to resume it later. A `leave` from the active run's board also writes any outcome still
waiting (see above). A run is never created from observations.

The client logs `held` and `board_mismatch` and shows the player nothing for either.

The entrance's roster pick and pre-entry board are kept apart from in-duty snapshots and are never
applied to a run, because a run's roster is fixed when it starts. They only prefill the website's
setup for the run the player is about to start, and lapse 30 minutes after their `observedAtUtc`,
or sooner when an unapplied `results` snapshot or a `leave` drops every kept snapshot. An
entrance upload answers `held` whether or not the character has an active run, with that run's
id as `runId` when it has one.

Status codes mirror `/sync` (400 `invalid_payload`, 401, the 403 family with echoed identity, 405,
413, 429 with its own per-token budget of 240/hour), plus **503 `sync_disabled`** when the global,
per-user, category or flag switch is off, and **503 `busy`** with `Retry-After` when the server
could not take the upload in time.

**Liveness.** Every upload answered `applied`, a heartbeat included, refreshes the run's "Plugin
connected" mark on the website, and a `leave` clears it. After a few minutes with no `applied`
upload (four by default) the mark turns to "Plugin disconnected"; a `board_mismatch` or `held`
upload does not touch it.

## Character binding

The plugin identifies a character by a **client-side SHA-256 of its ContentId** — the raw
ContentId (a ulong) never leaves the game client. The server treats the hash as an opaque
stable identifier; the only requirement is that the plugin computes the **same lowercase-hex
digest every session** (fix one byte representation of the ulong and never change it).

Ownership is proven only by the **Lodestone bio code** on the website. An upload writes to and
binds only a character the token's user has **verified**, and an upload never verifies a claim.

Resolution:

1. **Hash first.** A hash already bound to a character resolves directly — it is the durable
   identity, so it **survives renames and world transfers** even when the payload's
   name/world have drifted. The token's user must hold a verified claim on that character,
   else 403.
2. **First-upload binding.** An unknown hash falls back to matching `characterName` +
   `homeWorld` (both case-insensitive) against the token owner's claims, **pending or
   verified**. A single unbound match is bound (`bound: true` in the response) only when it is
   verified. Anything else returns a 403 naming why — the server never guesses, because binding
   the wrong character would write another character's data under this hash:
   - no claim matches → `character_not_claimed`;
   - every match is already bound to another hash → `character_bound_elsewhere`;
   - more than one unbound match, pending claims included → `character_ambiguous`;
   - exactly one unbound match, still pending → `character_not_verified`.

**Claims vs. favorites.** Only a *claimed* character is visible to the plugin surface; a
favorite (someone's non-claimed follow) is invisible — `/me` never lists it and the binder
never matches it. `/me` lists pending claims too, with `verified: false`.

**403 recovery.** Every 403 on `POST /sync`, `POST /occult/instance-state` and
`POST /crucible/observations` has the same shape:

```jsonc
{"error": "<code>", "name": "<payload characterName>", "world": "<payload homeWorld>"}
```

`<code>` is one of the four in the table below. None heals on retry; each needs the user to act.
A client halts every upload path on any of them and names the fix, whichever path was refused,
so a player who uses only one feature is told too.

| `error` | Meaning | What the plugin tells the player |
| --- | --- | --- |
| `character_not_claimed` | No claim matches, or the hash is bound to a character that is not theirs. Deliberately does not distinguish "no such character" from "not yours". | Claim the character on the website, then sync. The claim flow creates the character record, which the plugin cannot (it has no Lodestone id, so it never auto-creates characters). |
| `character_not_verified` | Their claim on this character is still pending. | Finish verification on the website (the Lodestone bio code), then sync. |
| `character_ambiguous` | Two or more of their unbound claims, pending or verified, share this name and world. | Remove all but one of those claims on the website, then sync. |
| `character_bound_elsewhere` | Every claim of theirs with this name and world is bound to a different game character. | The character is linked to a different game character; ask for help in Discord to relink it, then sync. |

A 403 code the plugin does not recognize, or a 403 without a readable body, gets the
`character_not_claimed` wording: claiming the character is the fix that applies to the most
players.

## Behavior the plugin author should know

- **`collectionScopes` — the one way absence becomes meaningful in an id list.** (`glamour`, a
  current-holdings snapshot rather than an id list, has its own rules.) A category's list
  normally proves only what IS present. Declaring it `"full"` asserts *this array is the
  character's complete set for this category at upload time* — send it only when the
  collector genuinely enumerated its whole domain and got an answer for every candidate.
  `"partial"`, or omitting the key or object, is always safe: it simply carries no
  evidence of absence. The server **never infers** completeness (a short list is
  indistinguishable from a small collection). An `unlock` upload is a delta and should
  report `"partial"` — the server accepts and acts on whatever it is told, so this one is
  the client's discipline rather than a validation the server enforces. Today
  `tripleTriadCards` and `orchestrionRolls` act on it: a `"full"` list stamps that category's
  snapshot marker on the character, which lets the site flag a manual mark made *before*
  that moment that the complete list contradicts ("Marked by you — the plugin didn't find
  it"). Nothing is ever auto-unmarked, and other categories' declarations are recorded
  and ignored. In this plugin the claim is declared per collection on
  `CategoryInfo.EnumeratesCompleteDomain` and carried through to the `CollectResult` — except
  `tamedBeasts`, which additionally requires the pass to have earned it (see its id-space bullet).
  A category may only declare `"full"` when its collector can enumerate everything the
  **server's catalog** may contain, not merely everything the game will answer for —
  `tripleTriadNpcs` withholds the claim for exactly that reason (see the **Triple Triad id
  spaces** note above, which describes the opponents the game keeps no beaten flag for). More categories declare `"full"` than the server acts on; a declaration the server
  records and ignores still has to be honest, because the server may begin acting on it
  without a plugin change.

  For the Triple Triad card and orchestrion catalogs, the server is known never to be *ahead*
  of a live client: each is imported from released-patch sheet data, and the game
  forces a client patch before login, so "client behind catalog" is unreachable while
  playing. The reverse skew — catalog behind a just-patched client — is harmless, because
  an id the catalog does not know is dropped and never becomes markable. That skew analysis is
  per-catalog, and has been done for these two. The other declaring categories rest on their own
  argument — for the sheet-backed ones, that the game answers for every sheet row the catalog
  draws from — documented beside each `CategoryInfo` in the plugin's collector registry.
- **`acquiredAt` timestamps.** An `unlock`-triggered upload stamps the upload moment as the
  acquisition time for every category in it. Snapshot uploads (`interval`/`login`/`manual`)
  stamp the upload time for achievements, minions, mounts, and Triple Triad cards, for
  Triple Triad NPCs (whose `acquiredAt` IS the "beaten at" moment), and for
  `orchestrionRolls` — whose acquisition comes from the provenance flags, but whose panel
  renders the date and for which the plugin is the only automatic channel, since the
  Lodestone does not publish rolls. **Quests are the one category left null.** An existing
  acquisition date is **never overwritten** by any upload. The Triple Triad categories stamp
  on the first write that marks a row acquired/beaten, identically for snapshot and unlock
  uploads (they share one write path), and the date is immutable thereafter.
- **Relic proofs from the item manifest.** Possession (`count > 0`) of a proof-scope item
  (the `relic-proofs` group, or the flat manifest) proves that relic stage **and every
  lower-order stage of the same relic**. Proofs are sticky: because possession is volatile
  (the stage-N weapon is consumed by stage N+1), an item absent from a later upload changes
  nothing.
- **Count-tracked items.** For ids in the materials and currencies groups, the reported
  counts are the current total, replacing the stored value — including downward, including
  to zero. This is a deliberate exception to grow-only semantics (`glamour` is the other), and
  it is scoped to counts: absence still never clears anything, and proof/collection flags
  remain monotonic.
  GC seals are three independent count-tracked currencies (every Grand Company's balance
  persists in the game and is reported; the website resolves which is spendable from the
  character's Lodestone affiliation). Which currency classes the plugin can read, and
  through which game mechanism, is documented in [currency-coverage.md](currency-coverage.md)
  — the reference for curating currency ids into manifest groups.
- **Rate limits and backoff.** The default limit is 60 uploads per token per hour. Honor
  `Retry-After` on 429 and 503 and back off — do not tight-loop retries.
- **Kill switches are server-enforced too.** A disabled category is stripped from the payload
  before any write; the stripped keys ride back in `skippedCategories` so the plugin can tell
  the user why a category didn't sync. Stripping covers both reasons a category can be off: the
  ops kill switch, and a feature flag the uploading account cannot see. `/config` only
  advertises a gate, so a client polling a stale config — or one that never reads `/config` at
  all — would otherwise still write rows it should not. A client that honors `categories` never
  reaches this: it simply changes the failure mode from a silent write to a visible strip.

## Forward compatibility

The two sides release independently. A newer plugin's unknown payload key is stripped and
logged, never an error; an older plugin simply omits keys, which is safe under monotonic
writes. Adding a collection on the plugin side is one new `ICollector` class (see the repo
`CLAUDE.md`); the per-category toggle and payload key then appear automatically.
