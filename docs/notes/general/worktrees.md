# Worktrees: the main checkout stays on main

- Leave the primary checkout (`UnitSportSwitzerland/`) on `main`, almost always. Several features
  run in parallel; switching branches there mixes up their uncommitted changes.
- Work on a branch in a sibling worktree: `git worktree add ../UnitSportSwitzerland-<issue#> -b feat/<issue#>-name main`
  (after `git pull` on `main`). Build, run and test from inside that worktree.
- Never `git checkout` / `git switch` in the main checkout. If you find it off `main`, say so
  instead of leaving it there.
- When the branch is merged: `git worktree remove ../UnitSportSwitzerland-<issue#>`.
- `git worktree list` shows what exists. To play a worktree's build: VS Code task `run: game from worktree`
  (`tools/run-worktree.ps1`, `.sh` on macOS/Linux) lists them, builds the one you pick and runs it.
- Checking a worktree by hand (Windows): `tools/wt.ps1 list` prints every worktree with its PR (number,
  state, title; `*` = uncommitted changes). `tools/wt.ps1 <issue#|list#|name> [play|net|two|server|editor]`
  builds it and starts Godot there with `--chunks` at the main checkout's terrain: `net` / `two` run a
  headless server on a free port (7800+) plus one or two clients (A, B) and stop it when they close;
  `-Clean` rebuilds `--no-incremental`, `-Scratch` keeps user:// out of the real save, `-- <args>` go to the game.
- Simple tasks (docs/notes tweaks, one-line fixes) need no branch or worktree: commit straight on
  `main` in the main checkout and push.
- **Never squash with `git reset --soft origin/main`** (#2035's PR #2041 reverted #2032, #2034 and #2038 that way):
  `refs/remotes/origin/main` is shared by every worktree, and another session's fetch moves it under you. A soft reset
  keeps your working tree (your old base plus your change) and records it on the new main: everything merged in between
  is silently undone. Squash onto your own base (`git reset --soft $(git merge-base HEAD origin/main)`), or merge or
  rebase onto main; and before merging, `git diff origin/main...HEAD --stat` must list only the files you meant.

