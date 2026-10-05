---
name: working-in-worktrees
description: Use when plugin work has to happen outside the main checkout (another session owns its dirty tree or the build loaded in-game, or the change needs a branch cut fresh from origin/main), and when loading a worktree's build in-game or removing a worktree after its PR merges.
---

# Working in worktrees

## Overview

A `git worktree` is a second checkout of this repo on its own branch. The **build** needs no
provisioning: `dotnet build` restores packages and the Dalamud SDK finds its libraries under
`%AppData%\XIVLauncher\addon\Hooks\dev`, so a fresh worktree builds and tests green as-is.

The hazard is **the game**. Every checkout builds the same `XIVShinies.SyncPlugin`, and they all
share one Dalamud dev-plugin list and one plugin config file. Another session's QA lives there.

## Setup

`<root>` is the main checkout's absolute path. Spell it out in every path: a relative path run
from inside a worktree nests the new worktree inside that one.

```bash
git fetch origin
git worktree add --no-track -b <type>/<slug> <root>/.claude/worktrees/<slug> origin/main
cp <root>/.claude/settings.local.json <root>/.claude/worktrees/<slug>/.claude/
git -C <root> check-ignore -v .claude/worktrees     # must print a rule
```

`<type>` is the change's commit type (`feat`, `fix`, `docs`, …). Then call **EnterWorktree with
`path`** set to `<root>/.claude/worktrees/<slug>`.

- **`--no-track`**: without it the branch's upstream is `origin/main`. `git status` then reports
  "ahead of origin/main", and the bare `git push` in `opening-pull-requests` fails on the
  name mismatch. Fix an existing branch with `git branch --unset-upstream`.
- **Enter the worktree.** `committing-code`, `opening-pull-requests` and `reviewing-code-changes`
  run bare `git` and `dotnet` in the session's working directory. Run from the main checkout,
  they diff, build and commit the *other* session's branch.
- **Invoke the worktree's skills.** After EnterWorktree, Claude Code lists each project skill
  twice: plain `committing-code` is the copy in the checkout the session started in, from
  whatever branch that has out; `.claude/worktrees/<slug>:committing-code` is this branch's.
  Use the prefixed one.
- **Manual `worktree add`, not EnterWorktree `name`**: the tool names the branch
  `worktree-<name>`, not the `feat/`/`fix/`/`docs/` names commits and PRs use.
- **`settings.local.json`** is gitignored, so a worktree starts without the `dotnet` allowlist.
  The copy stays ignored and is never committed.
- **`check-ignore`**: Claude Code lists `.claude/worktrees/` in `.git/info/exclude`, which keeps
  the worktree out of the main checkout's `git status` and searches. If nothing prints, add
  `**/.claude/worktrees/` to `.git/info/exclude` (not `.gitignore`).
- **Short `<slug>`**: the deepest build path is `<root>`, plus about 150 characters, plus the
  slug, and Windows `MAX_PATH` is 260.

## Build and test

Bare `dotnet build` and `dotnet test` from the worktree root; add `-c Release` when QA needs the
Release DLL. Before trusting a result, `git status --short` and `git diff --stat origin/main`
must show only your files.

Build only your own worktree. The DLL the game has loaded sits in another checkout's `bin`, and a
dev plugin with automatic reloading restarts the moment that DLL is rebuilt, mid-QA.

## Staging for a commit

`committing-code` leaves staging to the developer, and a Git GUI opened on the main checkout does
not show the worktree's changes. When asking them to stage, open their GUI on the worktree's
folder for them. For Sublime Merge that is `smerge <root>/.claude/worktrees/<slug>`.

## In-game QA of a worktree build

**Exactly one copy may be enabled.** Dalamud does not refuse a second plugin with the same name:
it logs a warning and loads both, so two copies each run syncs and write the same config.

Two toggles matter: disabling a plugin in `/xlplugins` unloads it now, and unticking its entry
under Dev Plugin Locations keeps Dalamud from loading it again. The maintainer clicks both. Never
edit `dalamudConfig.json` while the game runs, because Dalamud saves its in-memory copy over it.

1. **Ask first.** The maintainer tells the session that owns the loaded build (sessions cannot
   message each other): the swap interrupts its QA, and it must not rebuild or test until its
   copy is back.
2. **Unload the loaded copy:** disable it in `/xlplugins` → Dev Tools. Note which Dev Plugin
   Location it came from; that entry is "the original" below.
3. **Back up** `%AppData%\XIVLauncher\pluginConfigs\XIVShinies.SyncPlugin.json` to the
   scratchpad, now that nothing loaded can save over it. Every build shares this one file, and
   the plugin saves on load (migrations, the seen-collections baseline, auto-enable), so a
   branch whose settings differ drops or rewrites what the other branch stored.
4. **Load the worktree's copy:** `/xlsettings` → Experimental → Dev Plugin Locations → add
   `<worktree>\src\XIVShinies.SyncPlugin\bin\Release\XIVShinies.SyncPlugin.dll` (`bin\Debug` for
   `/shinies seedlog` or `dumpslots`), untick the original, **Save**, then enable the new copy
   in `/xlplugins`. It runs a real login sync with the shared token and backend about ten
   seconds after the character loads.
5. **Restore after QA**, in this order:
   1. Disable the worktree copy and remove its location.
   2. Copy the backup over the config file, while nothing is loaded.
   3. Re-tick the original, **Save**, and enable it.

   A loaded copy holds its config in memory and saves over a restore made while it runs, and
   saving the location list may load the original straight away.

## Cleanup after merge

Confirm the PR merged first: `-D` deletes the branch without checking. If the worktree's Dev
Plugin Location is still listed, remove it too, or Dalamud keeps pointing at a deleted path.

```bash
# ExitWorktree action "keep" first (it never deletes a worktree entered by path)
git worktree remove <root>/.claude/worktrees/<slug>
git branch -D <type>/<slug>                 # -d refuses: squash merges leave it "unmerged"
git ls-remote --heads origin <type>/<slug>  # if listed: git push origin --delete <type>/<slug>
```

Deleting the remote branch is a push, so it needs the maintainer's approval.

## Traps

| Trap | Symptom | Fix |
|------|---------|-----|
| Branch created without `--no-track` | "ahead of origin/main"; `git push` fails on the name mismatch | `git branch --unset-upstream` |
| A project skill run from the main checkout | Review or commit shows the other session's files | EnterWorktree `path` before invoking it |
| Relative paths in setup or cleanup | A worktree nested inside another, or `worktree remove` finds nothing | Absolute `<root>` paths |
| Two copies enabled | Every sync fires twice; `dalamud.log` has two `Finished loading XIVShinies.SyncPlugin` with no unload between | One enabled Dev Plugin Location |
| Config backed up or restored while a copy was loaded | Settings missing or changed after switching back | Back up and restore only with no copy loaded |
| Building another checkout | The loaded plugin reloads during someone's QA | Build only inside your own worktree |
| A tool called by quoted full path (`"/c/Program Files/GitHub CLI/gh.exe" …`) | Refused: the command "cannot be shown not to be git" | Call it by bare name: `gh`, `dotnet` |
