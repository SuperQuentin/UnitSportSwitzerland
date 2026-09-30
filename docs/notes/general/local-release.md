# Local release: `tools/release.sh`

- Releases are built and uploaded from your own machine, no GitHub Actions. Run from Git Bash on a synced `main`.
- `tools/release.sh --dry-run` prints the next version and changelog; `tools/release.sh` exports "Windows Desktop", zips it and runs `gh release create vX.Y.Z`.
- Semver from gitmoji commits since the last `v*` tag: `BREAKING` anywhere = major; anything except fix/docs/chore/merge = minor; only `:bug:` `:ambulance:` `:recycle:` `:art:` `:white_check_mark:` = patch; only `:memo:` `:wrench:` merges = no release.
- Stamps `config/version` into `project.godot` for the export, then restores the file. Output in `test_output/release/`.
- Needs `gh` logged in, dotnet, Godot mono with export templates (`GODOT=` overrides the path, see `godot-exe`).
- The export has no `terrain_chunks/`, so the released build uses the generated fallback world.
