# Changelog

All user-facing changes to XIV Shinies Sync, newest first.

Each `## vX.Y.Z — YYYY-MM-DD` section below is written for players, not developers, and doubles
as the GitHub release notes for that version: the release workflow copies the top section
verbatim into the release it publishes. Sections are added by the release flow (see
`.claude/skills/releasing/`), one per release, immediately under this line.

## v0.7.1 — 2026-09-02

- Sync the orchestrion rolls you have unlocked to XIV Shinies
- List orchestrion rolls in the plugin's installer description

## v0.7.0 — 2026-08-28

- Mark a switched-off collection with an Off chip and the site's reason
- Mark a collection you have never been shown with a New badge
- Extend auto-enable to collections, asking for your choice afresh
- Sort collections alphabetically in settings, status, and the log
- Fix the phantom job count shown in the upload log
- Stop listing a collection you switched off as unreadable

## v0.6.0 — 2026-08-14

- Share your Occult Crescent instance's encounters with the live tracker
- Sync your phantom jobs, knowledge level, and occult records
- Show in the sync status when job levels still need a Crescent visit
- Rename the items list to Tracked items and mark pickups in the log
- Group the consent list by game area with one line per collection

## v0.5.0 — 2026-08-02

- Sync the Triple Triad cards you have collected (opt-in)
- Sync the Triple Triad NPCs you have defeated (opt-in)
- Flag hand-marked cards the plugin could not find, for you to review
- Show each collection's details on hover to keep settings scannable

## v0.4.1 — 2026-07-30

- Show the plugin's icon and screenshots in the plugin installer

## v0.4.0 — 2026-07-18

- Sync your progress through multi-part relic turn-in quests (opt-in)
- Explain in settings why mannequin gear can't sync, with a workaround
- Draw the status chips as pills with breathing room when rows wrap
- Clarify the waiting message shown right after logging in
- Log outgoing sync data at Verbose level for troubleshooting

## v0.3.0 — 2026-07-16

- Count gear stored in dresser outfits toward your relic proofs
- Stop out-of-zone syncs from zeroing Occult Crescent currencies
- Show healthy sources as compact chips in the Reading from panel
- List currencies as their own source in the Reading from panel
- Announce new plugin releases in the XIV Shinies Discord

## v0.2.0 — 2026-07-15

- Sync your relic materials and currency balances (gil included) — the website's forge tray now fills and updates itself as you gather and spend
- Choose exactly which item groups to share: relic gear, materials, and currencies each get their own consent checkbox, and newly offered groups arrive switched off with a New badge
- The upload log reports relic steps the server actually proved ("2 new steps proven") instead of guessing from count changes
- A new "Reading from" panel shows what each sync could read, and names the container to open once when something can't be
- Item counts track high-quality and collectable copies separately
- The item scan is around ten times faster
- The upload log is now per character — it clears on logout

## v0.1.0 — 2026-07-11

- First release: your FFXIV collections sync to xiv-shinies.com automatically
- New quest, achievement, mount, and minion unlocks appear on the site within seconds
- Relic progress is proven by items you own — inventory, armoire, dresser, saddlebag, retainers
- Fully opt-in: a short setup shows what each collection sends before anything uploads
- A Recent uploads log shows every upload's outcome and exactly what was sent
- Your character is identified only by a one-way fingerprint computed on your machine
