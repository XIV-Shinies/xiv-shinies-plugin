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

- **First-party evidence.** A plugin upload comes from inside the game client, so it both
  verifies character ownership and outranks a Lodestone scrape; it cannot be erased by a
  manual unmark on the website.
- **Monotonic writes.** Collections only grow. An ID absent from a snapshot means "not read
  this time" (list not loaded, category disabled) — never "lost" — so a partial upload is
  always safe. Acquisition flags are set, never auto-unset, and rows are never deleted by a
  sync.

## Transport basics

- **Base URL:** `https://xiv-shinies.com` — user-overridable for local development.
- **`User-Agent: XIVShinies.SyncPlugin/<version>`** on every request.
- All endpoints live under `/api/plugin/v1/`. Every response body is JSON. Every
  authenticated success carries `Cache-Control: no-store` (per-token private data, and a
  cached kill switch would be a stale kill switch).
- Using the wrong method (POST to `/me` or `/config`, GET to `/sync`) returns **405**.

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
      "verified": true,        // the claim is verified (bio code or plugin upload)
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
    "items": true,
    "minions": true,
    "mounts": true,
    "questSequences": true,
    "quests": true,
    "tripleTriadCards": true,
    "tripleTriadNpcs": true
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
  client saves the round trips.
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
    "tripleTriadCards": [1, 475], // TripleTriadCard sheet row ids
    "tripleTriadNpcs": [2293762] // TripleTriadResident row ids (== TripleTriad row ids)
  },
  "collectionScopes": { // optional — per-category completeness; omitted key/object == "partial"
    "tripleTriadCards": "full" // "full" | "partial"
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
| `itemSources`            | optional object keyed by source name; each value `{state: "live"\|"cached"\|"unscanned"\|"loaded", count?: int, total?: int}` |
| `questSequences`         | object mapping quest id (digit-string key, ≤ 10 digits) → sequence byte (int 0–255), **max 100 entries** |
| `collectionScopes`       | optional object keyed by category name, each `"full"` \| `"partial"` exactly (anything else is a 400); omitted key or object == `"partial"` |

- **Unknown `collections` keys are stripped and logged, never rejected** — a plugin newer
  than the server keeps working (payload evolution is additive-only). An older plugin simply
  omits keys, which is safe under monotonic writes.
- **Ids the server's catalog does not recognize are ignored, never an error.** The
  plugin is a dumb fact-reader and should send every id the game reports; the server's
  catalog tables are deliberately pruned subsets (quests especially) and can trail the
  game after a patch. Each category's ids are filtered against its catalog before
  writing: unknown ids are dropped (and logged server-side), the known ids in the same
  payload still write, and the upload succeeds. Nothing is lost — the plugin re-sends
  everything on every sweep, so a dropped id lands as soon as the catalog imports it.
  Dropped ids are simply absent from the `written` counts.
- An **empty array carries no facts** and writes nothing (absence and emptiness are both "no
  information").
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
  (bags, equipped gear, the armoury chest, crystals); `currencies` covers the game's
  currency subsystem (gil, tomestones, scrips, and the rest), also read live. The accepted
  source keys are a **closed set** (`inventory`, `saddlebag`, `retainers`, `armoire`,
  `glamourDresser`, `currencies`) — an unrecognized key fails validation and rejects the
  whole upload, so a new source key ships server-first, and any source the plugin tracks
  for display only (an unreadable source such as mannequins) must stay off the wire.
- `fresh: false` means the count came from a cache rather than a live container read. The
  server treats a stale positive as a positive (the item *was* there), so the flag does not
  change the outcome.
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
  "storedSequences": 1 // present iff questSequences survived the strip and stored; NEW observations this upload (0 = all already known)
}
```

Optional keys are **omitted rather than null**, so the plugin can feature-detect them.
`items` never appears in `written` (it feeds relic proofs and count storage, not a
collection count). The plugin reads `written` as a plain category-keyed map, so a category
it has never heard of arrives intact and a server that names fewer causes no error.
`itemCounts` and `storedSequences` are informational, like `written`: the plugin ignores
them — no plugin logic may branch on them.

#### Status codes

| Status  | Body                                                            | Plugin behavior                                                                                                       |
| ------- | --------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| **200** | see above                                                       | Applied.                                                                                                             |
| **400** | `{"error": "invalid_payload", "issues": {…}}`                   | Validation failed; `issues` is `{fieldErrors, formErrors}`. A non-JSON body gets the same shape with a `formErrors` message. Don't retry unchanged. |
| **401** | `{"error": "invalid_token"}` + `WWW-Authenticate: Bearer`       | Token missing/malformed/unknown. Stop; user must generate a new token.                                               |
| **403** | `{"error": "character_not_claimed", "name": "…", "world": "…"}` | Character resolution failed. Render "claim `<name>` @ `<world>` on the website first". Don't retry until claimed.     |
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

## Character binding

The plugin identifies a character by a **client-side SHA-256 of its ContentId** — the raw
ContentId (a ulong) never leaves the game client. The server treats the hash as an opaque
stable identifier; the only requirement is that the plugin computes the **same lowercase-hex
digest every session** (fix one byte representation of the ulong and never change it).

Resolution:

1. **Hash first.** A hash already bound to a character resolves directly — it is the durable
   identity, so it **survives renames and world transfers** even when the payload's
   name/world have drifted. The token's user must hold a claim on that character, else 403.
2. **First-upload binding.** An unknown hash falls back to matching `characterName` +
   `homeWorld` (both case-insensitive) against the token owner's **claimed** characters.
   Exactly one candidate → the hash is bound to that character (`bound: true` in the
   response). Zero candidates and ambiguous matches both return the opaque 403 — the server
   never guesses, because binding the wrong character would write another character's data
   under this hash.
3. **Verification side-effect.** The first bound upload promotes the claim to verified: an
   in-game upload carrying the account's token is strong ownership evidence, so plugin users
   skip website bio verification.

**Claims vs. favorites.** Only a *claimed* character is visible to the plugin surface; a
favorite (someone's non-claimed follow) is invisible — `/me` never lists it and the binder
never matches it.

**403 recovery.** `character_not_claimed` deliberately does not distinguish "no such
character" from "not yours". The fix is always the same: **claim the character on the website
first** — the claim flow creates the character record, which the plugin cannot (it has no
Lodestone id, so it never auto-creates characters).

## Behavior the plugin author should know

- **`collectionScopes` — the one way absence becomes meaningful.** A category's list
  normally proves only what IS present. Declaring it `"full"` asserts *this array is the
  character's complete set for this category at upload time* — send it only when the
  collector genuinely enumerated its whole domain and got an answer for every candidate.
  `"partial"`, or omitting the key or object, is always safe: it simply carries no
  evidence of absence. The server **never infers** completeness (a short list is
  indistinguishable from a small collection). An `unlock` upload is a delta and should
  report `"partial"` — the server accepts and acts on whatever it is told, so this one is
  the client's discipline rather than a validation the server enforces. Today only
  `tripleTriadCards` acts on it: a `"full"` card list stamps the
  character's snapshot marker, which lets the site flag a manual mark made *before* that
  moment that the complete list contradicts ("Marked by you — the plugin didn't find
  it"). Nothing is ever auto-unmarked, and other categories' declarations are recorded
  and ignored. In this plugin the claim originates on the collector's `CollectResult`
  (`completeEnumeration`), so a new collector opts in without any downstream change.
  A category may only declare `"full"` when its collector can enumerate everything the
  **server's catalog** may contain, not merely everything the game will answer for —
  `tripleTriadNpcs` withholds the claim for exactly that reason (see the id-space note
  above). For cards the two agree, and the server has confirmed its catalog is never
  *ahead* of a live client: it is imported from released-patch sheet data, and the game
  forces a client patch before login, so "client behind catalog" is unreachable while
  playing. The reverse skew — catalog behind a just-patched client — is harmless, because
  an id the catalog does not know is dropped and never becomes markable.
- **`acquiredAt` timestamps.** An `unlock`-triggered upload stamps the upload moment as the
  acquisition time for every category in it. Snapshot uploads (`interval`/`login`/`manual`)
  stamp the upload time for achievements, minions, and mounts, and leave quests' date null.
  An existing acquisition date is **never overwritten**. The Triple Triad categories stamp
  the upload time on the first write that marks a row acquired/beaten, identically for
  snapshot and unlock uploads (they share one write path), and the date is immutable
  thereafter — for `tripleTriadNpcs` that date is the row's "beaten at" moment.
- **Relic proofs from the item manifest.** Possession (`count > 0`) of a proof-scope item
  (the `relic-proofs` group, or the flat manifest) proves that relic stage **and every
  lower-order stage of the same relic**. Proofs are sticky: because possession is volatile
  (the stage-N weapon is consumed by stage N+1), an item absent from a later upload changes
  nothing.
- **Count-tracked items.** For ids in the materials and currencies groups, the reported
  counts are the current total, replacing the stored value — including downward, including
  to zero. This is the one deliberate exception to grow-only semantics, and it is scoped to
  counts: absence still never clears anything, and proof/collection flags remain monotonic.
  GC seals are three independent count-tracked currencies (every Grand Company's balance
  persists in the game and is reported; the website resolves which is spendable from the
  character's Lodestone affiliation). Which currency classes the plugin can read, and
  through which game mechanism, is recorded in [currency-coverage.md](currency-coverage.md)
  — the reference for curating currency ids into manifest groups.
- **Rate limits and backoff.** The default limit is 60 uploads per token per hour. Honor
  `Retry-After` on 429 and 503 and back off — do not tight-loop retries.
- **Kill switches are server-enforced too.** A disabled category is stripped from the payload
  before any write; the stripped keys ride back in `skippedCategories` so the plugin can tell
  the user why a category didn't sync.

## Forward compatibility

The two sides release independently. A newer plugin's unknown payload key is stripped and
logged, never an error; an older plugin simply omits keys, which is safe under monotonic
writes. Adding a collection on the plugin side is one new `ICollector` class (see the repo
`CLAUDE.md`); the per-category toggle and payload key then appear automatically.
