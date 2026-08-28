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
| **Local player only** — never collect account IDs or data of other player characters, in any form, regardless of intended use (ban-enforced) | Only the local player is ever read: identity via `IPlayerState`, unlocks via `IUnlockState`, neither of which exposes other-player data. **No object-table or party-list access exists anywhere in the codebase.** The item scan reads the player's own storage only, the live occult tracker reads world state only, and the occult progression collectors read the local character's own data only — each is broken down below, because each touches an API that *could* have exposed another player and deliberately does not. | `Sync/CharacterIdentity.cs`, `Collectors/ItemCollector.cs`, `Occult/OccultInstanceReader.cs`, `Collectors/OccultProgressionCollector.cs`, `Collectors/OccultRecordsCollector.cs`, `Occult/KnowledgeObserver.cs` |
| **Hash player identifiers client-side** | The ContentId is SHA-256-hashed on the machine with a fixed byte representation (deterministic across sessions). The raw ulong never travels, is never logged, and is never persisted. Verified in-game: logs and config contain no raw ContentId. | `Sync/ContentIdHash.cs` |
| **HTTPS only, trusted CA, DNS hostname (never a raw IP)** | The backend URL is normalized and validated before any request: raw IP addresses are refused in every spelling (dotted, and the numeric/hex encodings the OS still resolves); plaintext HTTP is refused for remote hosts (tolerated only for loopback development); auto-redirects are disabled so the server cannot hand the token to an unvalidated host. The production server uses a Let's Encrypt certificate. A response body larger than a few MB is refused before it is buffered, so a hostile backend cannot exhaust the game's memory. | `Api/BackendUrl.cs`, `Api/ApiClient.cs` |
| **Backend URL user-overridable** | The base URL is a persisted setting, overridable by editing the plugin's config file (there is deliberately no UI for it). Because the token is sent to whatever host is configured, a non-default backend additionally requires setting an acknowledgment flag in the same config file — until it is set, the client refuses to send anything (unit tests prove zero requests leave, not merely an error status). Every user-facing sentence that says where data goes or where the user must act names the configured host, including the two identity-disclosure cards, and the profile link opens the configured server. The brand link row and the manifest punchline keep the official name, since neither describes the sync target. | `PluginSettings.BaseUrl`, `Api/ApiClient.cs`, `tests/…/ApiClientTests.cs` |
| **Minimize data sent** | Uploads carry ID numbers and item counts only — no names of things, no timestamps of acquisition, no inventory contents beyond the counts of items the server explicitly asked about. `collectionScopes` adds one per-category word saying whether the plugin read that collection completely; it names no id and is disclosed on the consent surface, because it lets the site flag a manual mark the plugin did not find. Currency balances (gil included) travel only for currency ids the server's manifest names AND the user's opted-in group covers, disclosed in the consent copy. Per-source scan states (`itemSources`) are status words and counts only — nothing identifies an individual retainer or container slot — and the consent copy names them, including the retainer count, because a headcount is a fact about the account rather than a count of any item asked about. Categories and groups the user did not opt into are never collected. | `Api/SyncRequest.cs`, `Collectors/CollectorGate.cs`, `Collectors/ManifestConsent.cs` |
| **Explicit opt-in before non-essential data collection; no silent first-run behavior** | Consent is enforced in code, not just reflected in UI: `UploadGate.CanContactServer` requires completed onboarding **and** the master switch **and** a usable token before any request, including the config poll — a fresh install talks to nobody. The wizard discloses what each category sends before the user can enable it, on a flat list with every checkbox visible. Every other consent control sits under the settings' outer Collections header, and it wears a "New" chip whenever anything beneath it has never been shown and the server permits it, so folding cannot bury a collection or an item group. Two consents stand for later, detailed below: the live occult tracker's toggle, and `AutoEnableNewFeatures`. Each setting is migrated OFF for configs written before it carried its present meaning — a silent default is consent by omission. | `Sync/UploadGate.cs`, `Occult/OccultGate.cs`, `PluginSettings.ApplyUpgradeMigrations`, `PluginSettings.AutoEnableUnseenCategories`, `Collectors/ManifestConsent.cs`, `Plugin.cs` (load order), `Windows/MainWindow.Wizard.cs`, `Windows/MainWindow.Consent.cs` |
| **No interaction with game servers without direct user action** | The plugin never interacts with the game's servers at all: it reads the local client's memory and speaks HTTPS to the XIV Shinies server only. | whole design; `Collectors/` |
| **No plugin-usage fingerprinting** | There is no analytics identifier of any kind. The auth token is a user-supplied credential, revocable on the website. The upload log is in-memory only and clears on unload. A development-build helper can fill it with fabricated rows for screenshots; it sits inside `#if DEBUG`, so it does not exist in any Release build. | `Sync/UploadLog.cs`, `Sync/UploadLogSeed.cs` |
| **Never block the framework thread** | Game state is read on the framework thread — every collector asserts this at runtime and refuses to run elsewhere. HTTP, JSON serialization, and retries run on background tasks (`Task.Run`); nothing calls `.Wait()`/`.Result` on a framework-thread task. Results cross back via volatile fields and atomic reference swaps. Every server-sized collection consumed on that thread is bounded: each manifest and the omit-when-unseen set at `CollectContext.MaxManifestItems`, the settings window's consent-group rows and the account panel's character list at fixed ceilings — so a hostile server cannot freeze the loop by inflating any list it controls. | `Collectors/GameThread.cs`, `Sync/SyncManager.cs`, `Collectors/CollectContext.cs`, `Collectors/CategorySettingsView.cs`, `Windows/MainWindow.Account.cs` |
| **Full teardown** | `Dispose()` mirrors the constructor exactly: every event subscription, command handler, window registration, and owned resource (fonts, HTTP client, cancellation sources) is released, in dependency order. Borrowed/framework-owned handles (the icon font, the shared mascot texture, injected services) are deliberately **not** disposed. Verified by a whole-repository census. | `Plugin.cs`, `Windows/MainWindow.cs` (the class spans `MainWindow.*.cs` partials; lifecycle lives here), `Sync/SyncManager.cs`, `Occult/OccultManager.cs`, `Occult/KnowledgeObserver.cs` |
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
  nothing, and the plugin declares completeness only for a collection it enumerated end to end.
- **Where "local player only" is load-bearing.** Three surfaces touch an API that could have
  exposed another player. The **item scan** reads the player's own storage: their retainers'
  inventories through `ItemFinderModule.RetainerInventories` **values** (the retainer-ID keys are
  never read and never leave the process; `RetainerManager` supplies a count only), their glamour
  dresser through `GlamourDresserItemIds` paired with `GlamourDresserItemSetUnlockBits`, and their
  currency balances through the game's `Currency` container plus `CurrencyManager`. The **live
  occult tracker** reads world state only — the instance's CE container
  (`PublicContentOccultCrescent`) and Dalamud's FATE table — carrying forward encounter ids,
  phases, and server timestamps alone; participant counts and positions never leave the reader.
  The **occult progression collectors** read the local character alone: phantom job levels and EXP
  from the instance director's state block (which holds no party fields), occult records from the
  character's own client-persisted save data (`MKDLoreModule.SeenLore`), and the knowledge level
  from exactly one backing value of the review window the player opens themselves.
- **Consent is code, not UI.** The gates (`UploadGate`, `CollectorGate`) are pure, unit-tested
  classes on the request path. Unchecking a box does not merely hide a button; it makes the
  request impossible.
- **The two consents that stand for later.** The live occult tracker's toggle is the one setting
  that defaults ON, defensible only because the ticked box is **visible on the wizard's consent
  step** before anything can send. `AutoEnableNewFeatures` defaults OFF and is the only route by
  which a collection is ever switched on without its own tick;
  `PluginSettings.AutoEnableUnseenCategories` acts on it at load and only there — for an onboarded
  install that ticked the box, only on collections this install has never shown, never on one
  whose scope depends on separately-answered consent groups, and never over a collection the user
  has been shown and switched off. Anything it switches on is wearing its "New" chip when the user
  next opens the window, or waiting to once the server permits the collection.
- **A collection the server has switched off is not introduced yet.** It raises no "New" chip and
  is not recorded as shown on any surface, so its introduction waits for the day it can actually
  be used, and nothing is collected for it meanwhile. The server may supply one sentence about it
  — `categoryNotes`, explaining why it is off — and that sentence's reach is bounded structurally:
  it renders only where the plugin's own "switched off" line would have, which is only under a
  collection the server disabled. It can never displace the collector-authored disclosure of what
  a collection sends, and can never appear beside a checkbox the user is able to tick.
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
  stack. Recorded because the plugin draws server-authored text at several sites and nothing in
  its own source states this — a future move to a binding that keeps printf semantics would
  introduce a real crash-and-disclose vector everywhere at once, silently.

## Keeping this document true

Any PR that adds a network call, reads a new game surface, registers a new event or window,
or touches identity data must update the relevant row here. A row that drifts from the code
is worse than no row at all.
