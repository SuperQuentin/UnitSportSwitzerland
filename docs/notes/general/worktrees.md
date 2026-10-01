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
- Simple tasks (docs/notes tweaks, one-line fixes) need no branch or worktree: commit straight on
  `main` in the main checkout and push.
