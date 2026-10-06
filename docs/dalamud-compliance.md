# Dalamud compliance, rule by rule

How this plugin satisfies [Dalamud's plugin guidelines](https://dalamud.dev/plugin-publishing/restrictions/)
and [technical considerations](https://dalamud.dev/plugin-development/technical-considerations/)
— with the evidence for each. This is a living document: it must
be updated whenever a change touches one of these surfaces, and it exists both to keep us
honest and to make official-repository review straightforward.

Last full audit: 2026-07-15, via a four-reviewer pre-release pass including whole-repository
censuses (teardown symmetry; category-generic rendering; group-key literals; per-category
response fields) and a field-by-field contract conformance audit.

| Rule | How this plugin complies | Where |
|---|---|---|
| **Local player only** — never collect account IDs or data of other player characters, in any form, regardless of intended use (ban-enforced) | Only the local player is ever read: identity via `IPlayerState`, unlocks via `IUnlockState`, and the character itself, for its appearance, via the game's `Control.GetLocalPlayer()` — none of which exposes other-player data. **No object-table or party-list access exists anywhere in the codebase.** Six surfaces touch an API that *could* have exposed another player and deliberately do not — the item scan, the glamour and appearance collectors, the live occult tracker, the occult progression collectors and the bestiary watcher; each is broken down under **Where "local player only" is load-bearing** below. | `Sync/CharacterIdentity.cs`, `Collectors/ItemCollector.cs`, `Collectors/GlamourCollector.cs`, `Collectors/StorageSources.cs`, `Collectors/AppearanceCollector.cs`, `Occult/OccultInstanceReader.cs`, `Collectors/OccultProgressionCollector.cs`, `Collectors/OccultRecordsCollector.cs`, `Occult/KnowledgeObserver.cs`, `Beastmaster/TamedBeastObserver.cs` |
| **Hash player identifiers client-side** | The ContentId is SHA-256-hashed on the machine with a fixed byte representation (deterministic across sessions). The raw ulong never travels, is never logged, and is never persisted. Verified in-game: logs and config contain no raw ContentId. | `Sync/ContentIdHash.cs` |
| **HTTPS only, trusted CA, DNS hostname (never a raw IP)** | The backend URL is normalized and validated before any request: raw IP addresses are refused in every spelling (dotted, and the numeric/hex encodings the OS still resolves); plaintext HTTP is refused for remote hosts (tolerated only for loopback development); auto-redirects are disabled so the server cannot hand the token to an unvalidated host. The production server uses a Let's Encrypt certificate. A response body larger than a few MB is refused before it is buffered, so a hostile backend cannot exhaust the game's memory. | `Api/BackendUrl.cs`, `Api/ApiClient.cs` |
| **Backend URL user-overridable** | The base URL is a persisted setting, overridable by editing the plugin's config file (there is deliberately no UI for it). Because the token is sent to whatever host is configured, a non-default backend additionally requires setting an acknowledgment flag in the same config file — until it is set, the client refuses to send anything (unit tests prove zero requests leave, not merely an error status). Every user-facing sentence that says where data goes or where the user must act names the configured host, including the two identity-disclosure cards, and the profile link opens the configured server. The brand link row and the manifest punchline keep the official name, since neither describes the sync target. | `PluginSettings.BaseUrl`, `Api/ApiClient.cs`, `tests/…/ApiClientTests.cs` |
| **Minimize data sent** | Uploads carry ID numbers, counts and raw game values only — no names of things, no timestamps of acquisition. Every collection names each kind of data it sends on its consent line, and categories and item groups the user did not opt into are never collected. Gear & glamour storage and character appearance stay unread and unsent until the server's `/config` names them. What each upload carries — item counts, gear and its storage, quality and dyes, appearance bytes, currencies, `itemSources`, `collectionScopes` — is broken down under **What each upload carries** below. | `Api/SyncRequest.cs`, `Collectors/CollectorGate.cs`, `Collectors/ManifestConsent.cs`, `Collectors/CollectorRegistry.cs`, `Glamour/GlamourFacts.cs`, `Appearance/AppearanceSnapshot.cs` |
| **Explicit opt-in before non-essential data collection; no silent first-run behavior** | Consent is enforced in code, not just reflected in UI: `UploadGate.CanContactServer` requires completed onboarding **and** the master switch **and** a usable token before any request, including the config poll — a fresh install talks to nobody. The wizard discloses what each category sends before the user can enable it, on a flat list with every checkbox visible. Every other consent control sits under the settings' outer Collections header, and it wears a "New" chip whenever anything beneath it has never been shown and the server permits it, so folding cannot bury a collection or an item group. Two consents stand for later, detailed below: the live occult tracker's toggle, and `AutoEnableNewFeatures`. Each setting is migrated OFF for configs written before it carried its present meaning — a silent default is consent by omission. | `Sync/UploadGate.cs`, `Occult/OccultGate.cs`, `PluginSettings.ApplyUpgradeMigrations`, `PluginSettings.AutoEnableUnseenCategories`, `Collectors/ManifestConsent.cs`, `Plugin.cs` (load order), `Windows/MainWindow.Wizard.cs`, `Windows/MainWindow.Consent.cs` |
| **No interaction with game servers without direct user action** | The plugin never interacts with the game's servers at all: it reads the local client's memory and speaks HTTPS to the XIV Shinies server only. | whole design; `Collectors/` |
| **No plugin-usage fingerprinting** | There is no analytics identifier of any kind. The auth token is a user-supplied credential, revocable on the website. The upload log is in-memory only and clears on unload. A development-build helper can fill it with fabricated rows for screenshots; it sits inside `#if DEBUG`, so it does not exist in any Release build. | `Sync/UploadLog.cs`, `Sync/UploadLogSeed.cs` |
| **Never block the framework thread** | Game state is read on the framework thread — every collector asserts this at runtime and refuses to run elsewhere. HTTP, JSON serialization, and retries run on background tasks (`Task.Run`); nothing calls `.Wait()`/`.Result` on a framework-thread task. Results cross back via volatile fields and atomic reference swaps. Every server-sized collection consumed on that thread is bounded: each manifest and the omit-when-unseen set at `CollectContext.MaxManifestItems`, the settings window's consent-group rows and the account panel's character list at fixed ceilings — so a hostile server cannot freeze the loop by inflating any list it controls. | `Collectors/GameThread.cs`, `Sync/SyncManager.cs`, `Collectors/CollectContext.cs`, `Collectors/CategorySettingsView.cs`, `Windows/MainWindow.Account.cs` |
| **Full teardown** | `Dispose()` mirrors the constructor exactly: every event subscription, command handler, window registration, and owned resource (fonts, HTTP client, cancellation sources) is released, in dependency order. Borrowed/framework-owned handles (the icon font, the shared mascot texture, injected services) are deliberately **not** disposed. Verified by a whole-repository census. | `Plugin.cs`, `Windows/MainWindow.cs` (the class spans `MainWindow.*.cs` partials; lifecycle lives here), `Sync/SyncManager.cs`, `Occult/OccultManager.cs`, `Occult/KnowledgeObserver.cs`, `Beastmaster/TamedBeastObserver.cs` |
| **Windowing API; no unprompted windows** | All UI goes through `WindowSystem`. The window opens only from `/shinies` (or its alias `/xivshinies`), the installer's open/settings buttons, or by the user's own navigation — never automatically on load or login. | `Plugin.cs`, `Windows/MainWindow.cs` |
| **Reproducible from public source** | No obfuscation, no downloading or loading of external code or native binaries at runtime, no self-updating, no timestamp/auto-increment versioning. Everything the plugin ships is in this repository. | whole repository |
| **Icon and imagery policy** | The plugin icon (512×512 PNG) and all shipped imagery are hand-made, not AI-generated, per Dalamud's AI policy. AI involvement in the *code* is disclosed centrally in [`AI-DECLARATION.md`](../AI-DECLARATION.md) (level: copilot), and will be declared in the official-repository submission PR. | `src/XIVShinies.SyncPlugin/images/icon.png`, `AI-DECLARATION.md` |

## Project conventions that go beyond the letter of the rules

- **Hashing is treated as a hard requirement.** Dalamud phrases client-side hashing as a
  recommendation ("whenever feasible, plugins should hash…"); this project treats it as
  non-negotiable.
- **Monotonic writes.** The server treats every upload as append-only: absence never clears a
  flag. The plugin reflects this — a category that could not be read is omitted from the
  payload, never sent as an empty list, so no partial upload can erase anything. A category
  the plugin declares it read *completely* is the one case where an absent id carries meaning,
  and even then the meaning is "worth your review", never a deletion: the server unmarks
  nothing, and the plugin declares completeness only for a collection whose read answers for
  every candidate the site's catalog can hold. The gear & glamour storage collection is the
  deliberate exception: it reports current holdings, so for a source read as current a missing
  piece reads as gone. The plugin keeps that honest by leaving out any container it did not read,
  withholding the whole collection rather than truncating it, and skipping it whenever it cannot be
  sure what it reads is current: while a storage window is open or a retainer bell is in use,
  among the other cases the API contract lists. Under the contract, the server clears
  its own record only on a second miss with every source current, at least 25 minutes after the
  first, and never clears a manual mark.
- **Where "local player only" is load-bearing.** Six surfaces touch an API that could have
  exposed another player. The **item scan** reads the player's own storage: their retainers'
  inventories through `ItemFinderModule.RetainerInventories` **values** (the retainer-ID keys are
  never read and never leave the process; `RetainerManager` supplies a count only), their glamour
  dresser through `GlamourDresserItemIds` paired with `GlamourDresserItemSetUnlockBits`, and their
  currency balances through the game's `Currency` container plus `CurrencyManager`. The **glamour
  collector** reads the same storage through the same gates (`StorageSources`), and adds two
  reads of the player's own: the live `RetainerMarket` container (the market listings of the
  retainer the player summoned most recently) and the game's live dresser copy (`MirageManager`)
  for dye bytes. Before reading, it asks only whether four storage windows
  (`MiragePrismPrismBox`, `MiragePrismPrismSetConvert`, `Cabinet`, `InventoryBuddy`) exist and
  whether the summoning-bell condition is set; it reads nothing from those windows. Both
  collectors also ask the game, through `UIState`, whether the local player has completed the
  quest that unlocks the chocobo companion, which decides whether the character has a saddlebag
  at all. The **appearance collector** reaches the character through
  `Control.GetLocalPlayer()`, the game's
  own reference to the local player, and copies its customization bytes, glasses ids, crest bits
  and display toggles from that one character's `DrawData`; the object table, where every nearby
  character lives, is never touched. The **live
  occult tracker** reads world state only — the instance's CE container
  (`PublicContentOccultCrescent`) and Dalamud's FATE table — carrying forward encounter ids,
  phases, and server timestamps alone; participant counts and positions never leave the reader.
  The **occult progression collectors** read the local character alone: phantom job levels and EXP
  from the instance director's state block (which holds no party fields), occult records from the
  character's own client-persisted save data (`MKDLoreModule.SeenLore`), and the knowledge level
  from exactly one backing value of the review window the player opens themselves. The **bestiary
  watcher** reads the Master's Bestiary window when the player opens it, taking the beast numbers,
  held flags, tile captions and the window's own held-out-of-total tally that it was handed to draw
  itself — the character's own collection, from a window about nobody else, and only while they
  have it on screen. It listens for that one window by name and reads nothing else; the chat and
  log-message channels, which carry more than the local character, are not subscribed to anywhere
  in the plugin.
- **What each upload carries.** Inventory contents travel in two scoped forms, each behind its own
  opt-in. The items scan sends counts only for the items the server explicitly asked about. The
  gear & glamour storage collection sends gear alone (items with an equip slot, soul crystals
  excluded — materials, consumables and other non-gear never travel): each piece's id and copy
  count, which pieces sit in the Glamour Dresser, its outfit glamours and the Armoire, and a loose
  dresser piece's quality and dyes. The character appearance collection sends the local
  character's own character-creator choices as the raw bytes the game stores (palette indices, not
  colors), the glasses worn, where the free company crest is shown, and four display toggles. Both
  of those stay unread and unsent until the server's `/config` names them (see below). Currency
  balances (gil included) travel only for currency ids the server's manifest names AND the user's
  opted-in group covers, disclosed in the consent copy.
  Per-source scan states (`itemSources`) are status words and counts only — nothing identifies an
  individual retainer or container slot — and both consent lines that send them name them,
  including the retainer count, because a headcount is a fact about the account rather than a
  count of any item asked about. The saddlebag's state also reflects whether the character has a
  saddlebag at all: one without a chocobo companion reports it read and empty. `collectionScopes`
  adds one per-category word saying whether the
  plugin read that collection completely; it names no id and is disclosed on the consent surface,
  because it lets the site flag a manual mark the plugin did not find.
- **Consent is code, not UI.** The gates (`UploadGate`, `CollectorGate`) are pure, unit-tested
  classes on the request path. Unchecking a box does not merely hide a button; it makes the
  request impossible.
- **When the plugin stops talking to the server.** A fresh install talks to nobody at all. Once
  syncing halts for something only the user can fix, whether the refusal came from a collection
  upload, the live tracker, or the config poll, it uploads nothing on either path (collections or
  the live tracker) and collects nothing for upload; whether it still polls `/config` depends on
  which halt it is. A character the server would not match (unclaimed, unverified, claimed twice,
  or linked elsewhere) leaves the poll running on its normal interval — `/config` answers that
  caller normally, and the poll is the only way a server-side change (a pause lifting, a category
  returning) reaches a plugin whose Sync now button a pause has disabled. A token-shaped halt stops
  the poll too, because `/config` is token-scoped and every request would draw the same refusal
  forever. The poll carries the token and nothing about the character. `Sync/SyncTickPlan.cs`
  holds the rule and is unit-tested.
- **The two consents that stand for later.** The live occult tracker's toggle is the one setting
  that defaults ON, defensible only because the ticked box is **visible on the wizard's consent
  step** before anything can send. When the server has the tracker switched off — for the feature
  alone, or by pausing everything — that box is drawn unticked and disabled, so the wizard cannot
  offer the choice at all; finishing setup in that state records the answer the user never got to
  give, and records it as OFF (`PluginSettings.SettleOccultConsent`). The default never survives a
  consent moment the user was not actually shown. `AutoEnableNewFeatures` defaults OFF and is the
  only route by which a collection is ever switched on without the user acting on the consent
  list; `PluginSettings.AutoEnableUnseenCategories` acts on it at load and only there — for an
  onboarded install that ticked the box, only on collections this install has never shown, never
  on one whose scope depends on separately-answered consent groups, never on one that declares
  `RequiresOwnOptIn` because it describes the character itself rather than what it has done or
  holds (the character's appearance), and never over a collection the user has been shown and
  switched off. `AutoEnableScope` holds the two exclusions that depend on the kind of collection,
  and is unit-tested. Anything it switches on is wearing its "New" chip when the user
  next opens the window, or waiting to once the server permits the collection.
- **A collection the server has switched off — or, for one that needs the server to name it, has
  not named — is not introduced yet.** It raises no "New" chip and is not recorded as shown on any
  surface, so its introduction waits for the day it can actually be used, and nothing is collected
  for it meanwhile. The server may supply one sentence about it — `categoryNotes`, explaining why
  it is off — and that sentence's reach is bounded structurally: it renders only where the
  plugin's own "switched off" line would have, which is only under a collection the server
  disabled. It can never displace the collector-authored disclosure of what a collection sends,
  and can never appear beside a checkbox the user is able to tick.
  Some collections need the server to name them before they count as offered at all — gear &
  glamour storage and character appearance declare this (`CategoryInfo.RequiresServerSupport`).
  For them a `/config` that never mentions the key, or no `/config` yet, reads as off rather than
  on, so neither is read or sent until the server names it, and the row says "Not offered by the
  server yet." rather than describing a decision nobody made. A note cannot replace that line
  until the server names the collection; once it does, even as off, the ordinary rules above apply.
- **`User-Agent: XIVShinies.SyncPlugin/<version>`** is sent on every request — our own
  convention, not a Dalamud rule, so the server can tell plugin traffic apart.
- **Every string adopted from the server is bounded before it is kept, drawn, or logged**
  (`Api/ServerText.cs`). The backend URL is user-overridable, which makes the server untrusted
  input: a value can be arbitrarily long, arrive as `null` where the contract promises a string,
  split a surrogate pair when cut, carry the newlines and control characters needed to lay out its
  own copy inside a panel it does not own, or hide invisible formatting that misrepresents the
  sentence itself — a bidirectional override reverses the reading order of everything after it, a
  zero-width space splits a word with nothing on screen to show for it, and both look innocent in a
  log. Those are dropped; the zero-width joiner and non-joiner are kept, since emoji sequences and
  Persian and Arabic spellings need them and neither can reorder or conceal anything. Bounding happens at adoption rather than at each
  draw, so a second surface showing the same string does not have to rediscover the problem.
- **Dalamud's ImGui text calls are unformatted, and that is load-bearing.** At API 15
  `Dalamud.Bindings.ImGui`'s `Text`, `TextColored`, `TextDisabled`, `TextWrapped` and
  `SetTooltip` all resolve to `igTextUnformatted`; the varargs `igText` family has no managed
  overload. A `%s` or `%n` in a server string therefore renders literally instead of reading the
  stack. The plugin draws server-authored text at several sites, so a binding with printf
  semantics would make every one of them a crash-and-disclose vector at once.

## Keeping this document true

Any PR that adds a network call, reads a new game surface, registers a new event or window,
or touches identity data must update the relevant row here. A row that drifts from the code
is worse than no row at all.
