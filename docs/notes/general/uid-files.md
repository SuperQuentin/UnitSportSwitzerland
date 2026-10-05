# .uid files: commit one with every script, never mint them twice

- Godot writes a sibling `.uid` file (one line, `uid://<random>`) for every `.cs`, `.gd`,
  `.gdshader` and `.gdshaderinc` it imports. Scenes and resources keep their UID inside the file
  instead, so they have no `.uid`.
- **Commit the `.uid` in the same commit as the script.** A script pushed without it lets every
  worktree's import mint its own value for that path, and when two of them are committed the merge
  is a `CONFLICT (add/add)` that a human has to resolve.
- The values are **not reproducible**: `src/Avatar/AirstairsMeshBuilder.cs` had the identical blob
  on `main` and on the #488 branch, and two different `.uid` blobs. No commit in the history has
  ever *modified* a `.uid`, so the damage only ever happens at first creation, in two places.
- `tools/`, `tests/`, `docs/`, `terrain_chunks/`, `test_output/` and `ressources/data/` each hold a
  `.gdignore` (`gdignore-data-dirs`), so Godot never imports them: the ~124 `.cs` files under
  `tools/` correctly have **no** `.uid`, and the guard skips any directory holding a `.gdignore`.
- Guarded in tier 0 by `UidFilesTests` (#503): it fails if an imported file has no `.uid` beside it,
  or a `.uid` has no source. Milliseconds, no Godot. Before it existed, 38 commits of the history
  were this cleanup — `:wrench: Missing .uid files`, `:see_no_evil: Untrack other features' .uid
  files the import made (#418)`, `:fire: Untrack .uid files of other features committed by mistake`.
- When it fails, run `<godot> --headless --import --path .` in the worktree and commit the `.uid`
  files it writes. **Two traps in that run:**
  - It mints `.uid` files for *every* script lacking one, including other features' scripts that
    happen to be in the tree. Add only the ones for your own scripts (`git add <path>.cs.uid`),
    never `git add -A`.
  - In a worktree whose Git LFS assets are still pointers, the import rewrites the textures'
    `.import` files to `valid=false` and drops their `dest_files`. Those 28 files are **not** to be
    committed: `git checkout --` them before staging.
- Exactly one script UID is referenced from a scene: `scenes/Main.tscn` -> `src/Core/Main.cs`
  (`uid://dwp85ey20g8h7`). Every other script is attached by path, so an add/add conflict on any
  other `.uid` can be resolved by taking either side.
