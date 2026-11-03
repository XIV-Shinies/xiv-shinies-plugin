# Dalamud compliance, rule by rule

How this plugin satisfies [Dalamud's plugin guidelines](https://dalamud.dev/plugin-publishing/restrictions/)
and [technical considerations](https://dalamud.dev/plugin-development/technical-considerations/),
with the evidence for each. It must be updated whenever a change touches one of these surfaces:
it exists to keep us honest and to make official-repository review straightforward.

Last full audit: 2026-07-15, via a four-reviewer pre-release pass including whole-repository
censuses (teardown symmetry; category-generic rendering; group-key literals; per-category
response fields) and a field-by-field contract conformance audit.

| Rule | How this plugin complies | Where |
|---|---|---|
| **Local player only** — never collect account IDs or data of other player characters, in any form, regardless of intended use (ban-enforced) | Only the local player is read: identity via `IPlayerState`, unlocks via `IUnlockState`, and the character itself via `Control.GetLocalPlayer()`, which takes no index and is not the object table. None of these exposes another player's data. **No object-table or party-list access exists anywhere in the codebase.** Seven surfaces use an API that *could* expose another player; see **Where "local player only" is load-bearing**. | `Sync/CharacterIdentity.cs`, `Collectors/ItemCollector.cs`, `Collectors/GlamourCollector.cs`, `Collectors/StorageSources.cs`, `Collectors/AppearanceCollector.cs`, `Occult/OccultInstanceReader.cs`, `Collectors/OccultProgressionCollector.cs`, `Collectors/OccultRecordsCollector.cs`, `Occult/KnowledgeObserver.cs`, `Beastmaster/TamedBeastObserver.cs`, `Beastmaster/Crucible/CrucibleObserver.cs`, `Beastmaster/Crucible/CrucibleManager.cs` |
| **Hash player identifiers client-side** | The ContentId and the player's own retainer ids are SHA-256-hashed on the machine with a fixed byte representation, so each digest is stable across sessions. The raw ids never travel, are never logged and are never persisted. Verified in-game for the ContentId: logs and config contain no raw value. | `Sync/ContentIdHash.cs`, `Glamour/RetainerIdHash.cs`, `Glamour/RetainerRoster.cs` |
| **HTTPS only, trusted CA, DNS hostname (never a raw IP)** | The backend URL is normalized and validated before any request: raw IPs are refused in every spelling (including the numeric and hex encodings the OS still resolves), plain HTTP is refused except for loopback development, and auto-redirects are off so the token cannot be handed to an unvalidated host. Production uses a Let's Encrypt certificate. A response body over a few MB is refused before it is buffered, so a hostile backend cannot exhaust the game's memory. | `Api/BackendUrl.cs`, `Api/ApiClient.cs` |
| **Backend URL user-overridable** | The base URL is a persisted setting, overridable in the plugin's config file (deliberately no UI). Because the token goes to whatever host is configured, a non-default backend also needs an acknowledgment flag in the same file; until it is set, no request leaves (unit-tested). Saving a different token drops the previous token's `/config` and holds syncs until the new token's answers, so one account's switches never govern another's uploads. User-facing text that says where data goes or where the user must act names the configured host, both identity-disclosure cards included, and the profile link opens it; the masthead tagline (the manifest's punchline) and brand link keep the official name, as neither names the sync target. | `PluginSettings.BaseUrl`, `Api/ApiClient.cs`, `SyncManager.NotifyTokenChanged`, `tests/…/ApiClientTests.cs` |
| **Minimize data sent** | Uploads carry ids, counts, progress figures (levels, EXP, ranks, achievement counts) and raw game values only, plus the character's name and home world, which the server needs to match the claimed character: no acquisition timestamps, and no other names except the player's own retainers' names in gear & glamour storage, which its consent line discloses. Crucible run snapshots (see **What the Crucible run sharing sends**) and achievement progress readings carry the moment each was read. Categories and item groups the user did not opt into are never collected. See **What each upload carries**. | `Api/SyncRequest.cs`, `Api/CrucibleObservationsRequest.cs`, `Collectors/CollectorGate.cs`, `Collectors/ManifestConsent.cs`, `Collectors/CollectorRegistry.cs`, `Glamour/GlamourFacts.cs`, `Appearance/AppearanceSnapshot.cs` |
| **Explicit opt-in before non-essential data collection; no silent first-run behavior** | `UploadGate.CanContactServer` requires completed onboarding, the master switch and a usable token before any request, the config poll included, so a fresh install talks to nobody. The wizard shows what each category and sharing feature sends, every checkbox in view, before it can be enabled. The Collections header wears a "New" chip while a collection, a group or the Crucible sharing beneath it is unseen and permitted, so folding hides none, including one `AutoEnableNewFeatures` switched on. A setting is migrated OFF for configs written before it carried its present meaning, as a silent default is consent by omission; the one migration that switches anything on grants the `legacy` item group only where the items consent covered it. See **The consents that stand for later**. | `Sync/UploadGate.cs`, `Occult/OccultGate.cs`, `Beastmaster/Crucible/CrucibleGate.cs`, `PluginSettings.ApplyUpgradeMigrations`, `PluginSettings.AutoEnableUnseenCategories`, `Collectors/ManifestConsent.cs`, `Plugin.cs`, `SyncManager.PollConfigAsync` (order), `Windows/MainWindow.Wizard.cs`, `Windows/MainWindow.Consent.cs` |
| **No parsing, raid logging, DPS meters or similar; no augmenting, altering or interfering with combat** | The plugin draws no overlay or combat UI, only its own settings window, and hooks nothing. During a fight, with the sharing on and only on a board, the Crucible run sharing reads the in-combat flag, the zone, and one thing more: which of the player's own familiars is out and its HP, as the game's pet bar shows it. It reads that once a second from the local player's own pet record (see the "local player only" bullet below), and reads no damage, actions, statuses, enemies, battle log or chat. Nothing read during a fight goes up until the in-combat flag clears, and then at most one list per fight, of up to three familiars' last HP. | `Beastmaster/Crucible/CrucibleManager.cs`, `Beastmaster/Crucible/CrucibleFamiliarTracker.cs` |
| **No interaction with game servers without direct user action** | The plugin never interacts with the game's servers: it reads the local client's memory and speaks HTTPS to the XIV Shinies server only. Achievement progress is read from the answer to the request the game makes when the player opens an achievement; the plugin never makes that request, and refuses any answer that lands while the Achievements window is shut, as it usually is when the game asks on its own at login. | whole design; `Collectors/`, `Achievements/AchievementProgressObserver.cs` |
| **No plugin-usage fingerprinting** | No analytics identifier of any kind. The auth token is a user-supplied credential, revocable on the website. The upload log lives in memory only, so unloading clears it; its screenshot seeder, which fills it with made-up rows, is inside `#if DEBUG` and absent from Release builds. | `Sync/UploadLog.cs`, `Sync/UploadLogSeed.cs` |
| **Never block the framework thread** | Game state is read on the framework thread: every collector asserts this at runtime and refuses to run elsewhere. The live tracker, the Crucible sharing, and the bestiary and achievement progress watchers read the game only inside the frame tick and window callbacks, and the Crucible sharing's name and familiar link sheets, all static game data, are read once at load. HTTP, request-body serialization and retries run on background tasks; nothing calls `.Wait()`/`.Result` on a framework-thread task. Results cross back through volatile fields and atomic reference swaps. Every server-sized list consumed on that thread is capped (each manifest and the omit-when-unseen set at `CollectContext.MaxManifestItems`, the settings window's consent-group rows and the account panel's character list at fixed ceilings), so a hostile server cannot freeze the game loop. | `Collectors/GameThread.cs`, `Sync/SyncManager.cs`, `Beastmaster/Crucible/CrucibleManager.cs`, `Beastmaster/TamedBeastObserver.cs`, `Collectors/CollectContext.cs`, `Collectors/CategorySettingsView.cs`, `Windows/MainWindow.Account.cs` |
| **Full teardown** | `Dispose()` mirrors the constructor: every event subscription, command handler, window registration and owned resource (fonts, HTTP client, cancellation sources) is released in dependency order. Borrowed handles (the icon font, injected services, and the mascot texture, which Dalamud caches and frees on its own) are deliberately **not** disposed. Verified by a whole-repository census. | `Plugin.cs`, `Windows/MainWindow.cs` (lifecycle of the `MainWindow.*.cs` partials), `Sync/SyncManager.cs`, `Occult/OccultManager.cs`, `Occult/KnowledgeObserver.cs`, `Beastmaster/TamedBeastObserver.cs`, `Achievements/AchievementProgressObserver.cs`, `Beastmaster/Crucible/CrucibleManager.cs`, `Beastmaster/Crucible/CrucibleObserver.cs` |
| **Windowing API; no unprompted windows** | All UI goes through `WindowSystem`. The window opens only from `/shinies` (or `/xivshinies`), the installer's open/settings buttons, or the user's own navigation, never on load or login. | `Plugin.cs`, `Windows/MainWindow.cs` |
| **Reproducible from public source** | No obfuscation, no downloading or loading of external code or native binaries at runtime, no self-updating, no timestamp/auto-increment versioning. Everything the plugin ships is in this repository. | whole repository |
| **Icon and imagery policy** | The plugin icon (512×512 PNG) and all shipped imagery are hand-made, not AI-generated, per Dalamud's AI policy. AI involvement in the *code* is disclosed in [`AI-DECLARATION.md`](../AI-DECLARATION.md) (level: copilot), and the official-repository submission PR must declare it too. | `src/XIVShinies.SyncPlugin/images/icon.png`, `AI-DECLARATION.md` |

## Project conventions that go beyond the letter of the rules

- **Hashing is a hard requirement.** Dalamud phrases client-side hashing as a recommendation
  ("whenever feasible, plugins should hash…"); this project treats it as non-negotiable.
- **Monotonic writes.** A category that could not be read is omitted, never sent empty, so no
  partial upload can erase anything. A completeness declaration, made only for a collection whose
  read covers the site's whole catalog, is the one way an absent id gains meaning, and only as
  "worth your review": the server unmarks nothing. Gear & glamour storage is the deliberate
  exception: it reports current holdings, so for a place read as current a missing piece reads as
  gone. The plugin keeps that honest by omitting any container it did not read, withholding the
  whole collection rather than truncating it, and skipping it whenever it cannot be sure its read
  is current. The API contract lists those cases and the server's clearing rule, which never
  clears a manual mark.
- **Where "local player only" is load-bearing.** Each of these touches an API that could expose
  another player, and reads only the local character's own data or shared world state:
  - **Item scan:** the player's own retainers' inventories (`ItemFinderModule.RetainerInventories`
    values only, never its retainer-id keys; a count only from `RetainerManager`), glamour dresser
    (`GlamourDresserItemIds`, `GlamourDresserItemSetUnlockBits`), currencies (`Currency` container,
    `CurrencyManager`) and, through `UIState`, whether the chocobo companion is unlocked.
  - **Glamour collector:** the same storage and gates (`StorageSources`), plus the live
    `RetainerMarket` and dresser (`MirageManager`) copies; the retainer map's keys (own retainer
    ids, hashed before they leave) and, from `RetainerManager`, names, listing counts and last
    selection; and whether four storage windows exist or a summoning bell is in use, reading
    nothing from them.
  - **Appearance collector:** the `DrawData` and model id (`ModelContainer.ModelCharaId`, read to
    skip a transformed character) of the character `Control.GetLocalPlayer()` returns; the object
    table, where every nearby character lives, is never touched.
  - **Live occult tracker:** world state only (the instance's CE container,
    `PublicContentOccultCrescent`, and Dalamud's FATE table); only encounter ids, phases and server
    timestamps leave the reader, with the territory and current world. Participant counts and
    positions never do.
  - **Occult progression collectors:** phantom job levels and EXP from the instance director's
    state block (no party fields), occult records from the character's own save data
    (`MKDLoreModule.SeenLore`), and the knowledge level from one value of the review window the
    player opens.
  - **Bestiary watcher** (`TamedBeastObserver`): the open Master's Bestiary window (beast numbers,
    held flags, captions, tally); login state, zone and bestiary size; and the character's beast
    ranks, loaded into a handler found by event id alone, and only for the client talking to the
    Crucible NPC. Their HP figures are never sent; a Debug build's `/shinies dumpranks` logs them.
  - **Crucible run sharing** (`CrucibleObserver`): out of combat, the Crucible's own windows,
    skipping board enemy rows (the team window lists only the player's familiars), and the
    character's own HP; in a fight, only its own pet's data id and HP (`IBuddyList.PetBuddy`),
    never its `GameObject`, an object-table lookup. Nothing in the plugin subscribes to chat or log
    messages.
- **What each upload carries.** Each category sits behind its own opt-in, and its consent line
  names what it sends.
  - **Every upload:** the hashed ContentId, the character's name and home world (to match the
    character claimed on the site), the plugin version and the trigger.
  - **Items:** counts only, for the items the server asked about.
  - **Gear & glamour storage:** gear only (equip-slot items, soul crystals excluded): each piece's
    id and copy count per place, down to the retainer and its market listings (`market: true`);
    which pieces sit in the Glamour Dresser, outfit glamours and the Armoire; a loose dresser
    piece's quality and dyes; and each read retainer's hashed id and, when the game has it, name.
  - **Character appearance:** the character-creator bytes as the game stores them, the glasses
    worn, where the free company crest shows, and four display toggles.
  - **Currencies:** balances (gil included), only for ids the server's manifest names and the
    user's opted-in group covers.
  - **`itemSources`:** a status word and counts per storage source; nothing identifies a retainer
    or a slot. Both consent lines that send it name it, the retainer count included, since a
    headcount is a fact about the account, not a count of any item asked about.
  - **`collectionScopes`:** one word per category saying whether it was read completely, and no
    ids. Disclosed on the consent surface, since it lets the site flag a manual mark the complete
    list does not hold.
  - **Crucible runs:** see **What the Crucible run sharing sends**.
- **What the Crucible run sharing sends.** Snapshots of what the Crucible's windows showed, as
  ids, counts and figures, each with the moment it was read or its window closed, in uploads that
  name the territory their snapshots were read in. The roster pick and the board seen before
  entering go up from the entrance; everything else from a board, where an empty heartbeat, which
  reads nothing and so also goes out mid-fight, says the plugin is still there, and an empty
  upload marks leaving:
  - **Board:** the view, the piece being played, and each piece's progress.
  - **Team:** the window's mode; each of the player's own familiars with its HP, rank (and whether
    the board capped it), feeds eaten, lineup place and whether it is resting; and the feed or horn
    being given.
  - **Bag:** the token balance and the item and gear ids.
  - **Offers:** whether treasure, loot or shop, the token balance and each offered item; on a shop
    its price, discount and whether it was bought; on loot, the tokens the fight paid.
  - **Results:** the score lines (enemies, elites and bosses beaten, and their points), each
    bonus's points, and each familiar's rank and EXP before and after. Mode, rank and bonus names
    go up as sheet ids (`Addon`, `XBMScoreRank`, `XBMScoreBonus`, read in the client's language),
    or as null when a name does not resolve, never as text the game drew.
  - **The character:** its own HP.
  - **Familiars:** once a fight is over, each familiar the player had out during it, with its HP as
    it left the field or as the fight ended, when that was read, and whether it is still out;
    outside a fight, the familiar that is out when its HP changes. Each goes up as its `XBMPet`
    row, which the `XBMPet` sheet's link to the pet's `Pet` row gives.
- **Consent is code, not UI.** The gates (`UploadGate`, `CollectorGate`, `CrucibleGate`) are pure,
  unit-tested classes on the request path. Unchecking a box makes the request impossible, not
  merely hidden.
- **When the plugin stops talking to the server.** Once a refusal on any path, the `/config` poll
  included, halts syncing for something only the user can fix, nothing is uploaded on any path, no
  collection pass runs, and the live tracker and the Crucible sharing read nothing. A character
  the server would not match (unclaimed, unverified, claimed twice, or linked elsewhere) keeps the
  `/config` poll on its normal interval: `/config` still answers it, and the poll is how a
  server-side change (a pause lifting, a category returning) reaches the plugin unprompted. A token
  refusal stops the poll too, since `/config` is token-scoped and would refuse every poll. The poll
  carries the token and nothing about the character. `Sync/SyncTickPlan.cs` holds the rule and is
  unit-tested.
- **When syncing is switched off on the website.** A 503 `sync_disabled` on any path brings the
  next `/config` poll forward to two minutes away, never pushing back one due sooner; once
  `/config` says syncing is off, every path is gated locally, so no further refusal arrives to
  bring it forward again. The refusal's wait ends early only on a poll started after it that says
  the path is on, and a second refusal inside a wait that ended early, with no upload accepted in
  between, waits its whole `Retry-After` (`Sync/SyncResumeWait.cs`). During a pause, a Sync now
  press asks `/config` once instead of syncing, at most once every ten seconds however fast the
  button is pressed, and syncs only if the answer says syncing is back on (`Sync/PausedPress.cs`).
- **The consents that stand for later.** Three answers reach past the moment they were given:
  - **Live occult tracker:** the one toggle that defaults ON, defensible only because the ticked
    box is **visible on the wizard's consent step** before anything can send. With the tracker or
    all syncing off server-side, the box is unticked and disabled and setup records OFF
    (`PluginSettings.SettleOccultConsent`), so the default never outlives a choice the user could
    not make.
  - **Crucible run sharing:** defaults OFF and only the user turns it on, since it shares their
    play as it happens. Its gate (`CrucibleGate`) also needs the server's `crucibleRuns` switch,
    and nothing about the player's play is read while the gate is closed.
  - **`AutoEnableNewFeatures`:** defaults OFF, the only way a collection starts without its own
    tick; its copy covers each new collection, whatever it sends. Past onboarding,
    `PluginSettings.AutoEnableUnseenCategories` applies it to unseen, unanswered collections without
    separate groups, marked New: at load to each needing no naming, on each `/config` to each the
    server permits.
- **A collection the server has switched off, or one that needs the server to name it and has not
  been named, is not introduced yet.** It raises no "New" chip, is not recorded as shown, and
  nothing is collected for it. The server may send one sentence explaining why (`categoryNotes`);
  it renders only in the Off chip's tooltip, where the plugin's own off text would, never
  replaces the disclosure of what a collection sends, and never sits beside a checkbox the user
  can tick. The collections that need naming are gear & glamour storage and character appearance
  (`CategoryInfo.RequiresServerSupport`); each one's row says it is not offered, or before any
  `/config` that it is waiting for the answer.
- **`User-Agent: XIVShinies.SyncPlugin/<version>`** is sent on every request: our own
  convention, not a Dalamud rule, so the server can tell plugin traffic apart.
- **Every string adopted from the server is bounded before it is kept, drawn or logged**
  (`Api/ServerText.cs`). A user-overridable backend makes the server untrusted input, so each
  string is length-capped without splitting a surrogate pair, and display copy
  (`ServerText.SingleLine`) is also null-safe and folded onto one line: newlines and control
  characters collapse to a space, so a string cannot lay out copy of its own in a panel it does not
  own, and invisible formatting that can reorder or hide text (bidirectional overrides, zero-width
  spaces) is dropped. The zero-width joiner and non-joiner are kept for emoji and for Persian and
  Arabic spelling, and neither can reorder or hide anything. Bounding happens once, at adoption, so
  no draw site has to repeat it.
- **Dalamud's ImGui text calls are unformatted, and that is load-bearing.** At API 15,
  `Dalamud.Bindings.ImGui`'s `Text`, `TextColored`, `TextDisabled`, `TextWrapped` and `SetTooltip`
  all resolve to `igTextUnformatted`, and the varargs `igText` family has no managed overload, so
  a `%s` or `%n` in a server string renders literally instead of reading the stack. Moving to a
  binding with printf semantics would silently make every site that draws server text a
  crash-and-disclose vector.

## Keeping this document true

Any PR that adds a network call, reads a new game surface, registers a new event or window,
or touches identity data must update the relevant row here. A row that drifts from the code
is worse than no row at all.
