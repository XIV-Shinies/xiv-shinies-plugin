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
| **Local player only** — never collect account IDs or data of other player characters, in any form, regardless of intended use (ban-enforced) | Only the local player is read: identity via `IPlayerState`, unlocks via `IUnlockState`, and the character itself via `Control.GetLocalPlayer()`. **No object-table or party-list access exists anywhere in the codebase.** Six surfaces use an API that *could* expose another player; see **Where "local player only" is load-bearing**. | `Sync/CharacterIdentity.cs`, `Collectors/ItemCollector.cs`, `Collectors/GlamourCollector.cs`, `Collectors/StorageSources.cs`, `Collectors/AppearanceCollector.cs`, `Occult/OccultInstanceReader.cs`, `Collectors/OccultProgressionCollector.cs`, `Collectors/OccultRecordsCollector.cs`, `Occult/KnowledgeObserver.cs`, `Beastmaster/TamedBeastObserver.cs` |
| **Hash player identifiers client-side** | The ContentId and the player's own retainer ids are SHA-256-hashed on the machine with a fixed byte representation, so each digest is stable across sessions. The raw ids never travel, are never logged and are never persisted. Verified in-game for the ContentId: logs and config contain no raw value. | `Sync/ContentIdHash.cs`, `Glamour/RetainerIdHash.cs`, `Glamour/RetainerRoster.cs` |
| **HTTPS only, trusted CA, DNS hostname (never a raw IP)** | The backend URL is validated before any request: raw IPs are refused in every spelling (including the numeric and hex encodings the OS still resolves), plain HTTP is refused except for loopback development, and auto-redirects are off so the token cannot be handed to an unvalidated host. Production uses a Let's Encrypt certificate. A response body over a few MB is refused before it is buffered. | `Api/BackendUrl.cs`, `Api/ApiClient.cs` |
| **Backend URL user-overridable** | The base URL is a persisted setting, overridable in the plugin's config file (deliberately no UI). Because the token goes to whatever host is configured, a non-default backend also needs an acknowledgment flag in the same file; until it is set, no request leaves (unit-tested). User-facing text that says where data goes names the configured host; the masthead tagline and brand link keep the official name, as neither names the sync target. | `PluginSettings.BaseUrl`, `Api/ApiClient.cs`, `tests/…/ApiClientTests.cs` |
| **Minimize data sent** | Uploads carry ids, counts and raw game values only, plus the character's name and home world, which the server needs to match the claimed character: no acquisition timestamps, and no other names except the player's own retainers' names in gear & glamour storage, which its consent line discloses. Categories and item groups the user did not opt into are never collected. See **What each upload carries**. | `Api/SyncRequest.cs`, `Collectors/CollectorGate.cs`, `Collectors/ManifestConsent.cs`, `Collectors/CollectorRegistry.cs`, `Glamour/GlamourFacts.cs`, `Appearance/AppearanceSnapshot.cs` |
| **Explicit opt-in before non-essential data collection; no silent first-run behavior** | `UploadGate.CanContactServer` requires completed onboarding, the master switch and a usable token before any request, the config poll included, so a fresh install talks to nobody. The wizard shows what each category sends, every checkbox in view, before it can be enabled. In settings, the Collections header wears a "New" chip while anything beneath it has never been shown, so folding cannot bury a new collection or item group, including one `AutoEnableNewFeatures` switched on. A setting is migrated OFF for configs written before it carried its present meaning; the one migration that switches anything on grants the `legacy` item group only to a user whose items consent already covered it. See **The two consents that stand for later**. | `Sync/UploadGate.cs`, `Occult/OccultGate.cs`, `PluginSettings.ApplyUpgradeMigrations`, `PluginSettings.AutoEnableUnseenCategories`, `Collectors/ManifestConsent.cs`, `Plugin.cs` (load order), `Windows/MainWindow.Wizard.cs`, `Windows/MainWindow.Consent.cs` |
| **No interaction with game servers without direct user action** | The plugin never interacts with the game's servers: it reads the local client's memory and speaks HTTPS to the XIV Shinies server only. | whole design; `Collectors/` |
| **No plugin-usage fingerprinting** | No analytics identifier of any kind. The auth token is a user-supplied credential, revocable on the website. The upload log lives in memory only; its screenshot seeder is inside `#if DEBUG` and absent from Release builds. | `Sync/UploadLog.cs`, `Sync/UploadLogSeed.cs` |
| **Never block the framework thread** | Game state is read on the framework thread, which every collector asserts at runtime. HTTP, serialization and retries run on background tasks; nothing calls `.Wait()`/`.Result` on a framework-thread task. Every server-sized list consumed on that thread is capped (`CollectContext.MaxManifestItems`, and fixed ceilings in the settings and account panels), so a hostile server cannot freeze the game loop. | `Collectors/GameThread.cs`, `Sync/SyncManager.cs`, `Collectors/CollectContext.cs`, `Collectors/CategorySettingsView.cs`, `Windows/MainWindow.Account.cs` |
| **Full teardown** | `Dispose()` mirrors the constructor: every event subscription, command handler, window registration and owned resource (fonts, HTTP client, cancellation sources) is released in dependency order. Borrowed handles (the icon font, the shared mascot texture, injected services) are deliberately **not** disposed. Verified by a whole-repository census. | `Plugin.cs`, `Windows/MainWindow.cs` (lifecycle of the `MainWindow.*.cs` partials), `Sync/SyncManager.cs`, `Occult/OccultManager.cs`, `Occult/KnowledgeObserver.cs`, `Beastmaster/TamedBeastObserver.cs` |
| **Windowing API; no unprompted windows** | All UI goes through `WindowSystem`. The window opens only from `/shinies` (or `/xivshinies`), the installer's open/settings buttons, or the user's own navigation, never on load or login. | `Plugin.cs`, `Windows/MainWindow.cs` |
| **Reproducible from public source** | No obfuscation, no downloading or loading of external code or native binaries at runtime, no self-updating, no timestamp/auto-increment versioning. Everything the plugin ships is in this repository. | whole repository |
| **Icon and imagery policy** | The plugin icon (512×512 PNG) and all shipped imagery are hand-made, not AI-generated, per Dalamud's AI policy. AI involvement in the *code* is disclosed in [`AI-DECLARATION.md`](../AI-DECLARATION.md) (level: copilot), and the official-repository submission PR must declare it too. | `src/XIVShinies.SyncPlugin/images/icon.png`, `AI-DECLARATION.md` |

## Project conventions that go beyond the letter of the rules

- **Hashing is a hard requirement.** Dalamud phrases client-side hashing as a recommendation
  ("whenever feasible, plugins should hash…"); this project treats it as non-negotiable.
- **Monotonic writes.** A category that could not be read is omitted, never sent empty, so no
  partial upload can erase anything. A completeness declaration, made only for a collection whose
  read covers the site's whole catalog, is the one way an absent id gains meaning, and only as
  "worth your review": the server unmarks nothing. Gear & glamour storage is
  the deliberate exception: it reports current holdings, so for a place read as current a missing
  piece reads as gone. The plugin keeps that honest by omitting any container it did not read,
  withholding the whole collection rather than truncating it, and skipping it whenever it cannot
  be sure its read is current. The API contract lists those cases and the server's clearing rule,
  which never clears a manual mark.
- **Where "local player only" is load-bearing.** Each of these touches an API that could expose
  another player, and reads only the local character's own data or shared world state:
  - **Item scan:** the player's own retainers' inventories (`ItemFinderModule.RetainerInventories`
    values only, never its retainer-id keys; a count only from `RetainerManager`), glamour dresser
    and currency balances, and, through `UIState`, whether the chocobo companion is unlocked.
  - **Glamour collector:** the same storage and gates (`StorageSources`), plus the live
    `RetainerMarket` and dresser (`MirageManager`) copies; the retainer map's keys (own retainer
    ids, hashed before they leave) and, from `RetainerManager`, names, listing counts and last
    selection; and whether four storage windows exist or a summoning bell is in use, reading
    nothing from them.
  - **Appearance collector:** the `DrawData` of the character `Control.GetLocalPlayer()` returns;
    the object table, where every nearby character lives, is never touched.
  - **Live occult tracker:** world state only (`PublicContentOccultCrescent` and Dalamud's FATE
    table); only encounter ids, phases and timestamps leave the reader, with the territory and
    current world. Participant counts and positions never do.
  - **Occult progression collectors:** the instance director's state block (no party fields), the
    character's own save data (`MKDLoreModule.SeenLore`), and one value of a window the player
    opens.
  - **Bestiary watcher:** the Master's Bestiary window, by name, while the player has it open: its
    beast numbers, held flags, tile captions and held-of-total tally. No chat or log-message
    channel is subscribed to anywhere in the plugin.
- **What each upload carries.** Each category sits behind its own opt-in, and its consent line
  names what it sends.
  - **Every upload:** the hashed ContentId, the character's name and home world (to match the
    character claimed on the site), the plugin version and the trigger.
  - **Items:** counts only, for the items the server asked about.
  - **Gear & glamour storage:** gear only (equip-slot items, soul crystals excluded): each piece's
    id and copy count per place, down to the retainer; which pieces sit in the Glamour Dresser,
    outfit glamours and the Armoire; a loose dresser piece's quality and dyes; and each read
    retainer's hashed id and, when the game has it, name.
  - **Character appearance:** the character-creator bytes as the game stores them, the glasses
    worn, where the free company crest shows, and four display toggles.
  - **Currencies:** balances (gil included), only for ids the server's manifest names and the
    user's opted-in group covers.
  - **`itemSources`:** a status word and counts per storage source, including the retainer count;
    nothing identifies a retainer or a slot. Both consent lines that send it name it.
  - **`collectionScopes`:** one word per category saying whether it was read completely, and no
    ids. Disclosed on the consent surface.
- **Consent is code, not UI.** The gates (`UploadGate`, `CollectorGate`) are pure, unit-tested
  classes on the request path. Unchecking a box makes the request impossible, not merely hidden.
- **When the plugin stops talking to the server.** Once syncing halts for something only the
  user can fix, nothing is collected or uploaded, on either the collections or the live-tracker
  path. A character the server would not match (unclaimed, unverified, claimed twice, or linked
  elsewhere) keeps the `/config` poll on its normal interval, since the poll is how a server-side
  change reaches a paused plugin; a token refusal stops the poll too, since every request would
  draw the same refusal. The poll carries the token and nothing about the character.
  `Sync/SyncTickPlan.cs` holds the rule and is unit-tested.
- **The two consents that stand for later.** The live occult tracker's toggle is the one setting
  that defaults ON, defensible because the ticked box is **visible on the wizard's consent step**
  before anything can send; with the tracker off server-side, the box is disabled and setup records
  OFF (`PluginSettings.SettleOccultConsent`). `AutoEnableNewFeatures` defaults OFF and is the only
  way a collection is switched on without its own tick; ticking it consents to each collection
  added later and all it sends, whatever kind of data it is.
  `PluginSettings.AutoEnableUnseenCategories` applies it at load on an onboarded install, only to
  never-shown collections with no recorded answer and no separately answered groups
  (`ManifestConsent.FixedScopeCategoryKeys`).
- **A collection the server has switched off, or one that needs the server to name it and has not
  been named, is not introduced yet.** It raises no "New" chip, is not recorded as shown, and
  nothing is collected for it. The server may send one sentence explaining why (`categoryNotes`);
  it renders only in the Off chip's tooltip, where the plugin's own off text would, never
  replaces the disclosure of what a collection sends, and never sits beside a checkbox the user
  can tick. A collection that needs the server to name it (`CategoryInfo.RequiresServerSupport`:
  gear & glamour storage, character appearance) reads as off until `/config` names it, and its
  row says so.
- **`User-Agent: XIVShinies.SyncPlugin/<version>`** is sent on every request: our own
  convention, not a Dalamud rule, so the server can tell plugin traffic apart.
- **Every string adopted from the server is bounded before it is kept, drawn or logged**
  (`Api/ServerText.cs`). A user-overridable backend makes the server untrusted input, so each
  string is length-capped without splitting a surrogate pair, and display copy
  (`ServerText.SingleLine`) is also null-safe and folded onto one line: newlines and control
  characters collapse to a space, and invisible formatting that can reorder or hide text
  (bidirectional overrides, zero-width spaces) is dropped. The zero-width joiner and non-joiner are
  kept for emoji and for Persian and Arabic spelling. Bounding happens once, at adoption.
- **Dalamud's ImGui text calls are unformatted, and that is load-bearing.** At API 15,
  `Dalamud.Bindings.ImGui`'s `Text`, `TextColored`, `TextDisabled`, `TextWrapped` and `SetTooltip`
  all resolve to `igTextUnformatted`, and the varargs `igText` family has no managed overload, so
  a `%s` or `%n` in a server string renders literally. With printf semantics, every site that
  draws server text would be a crash-and-disclose vector.

## Keeping this document true

Any PR that adds a network call, reads a new game surface, registers a new event or window,
or touches identity data must update the relevant row here. A row that drifts from the code
is worse than no row at all.
