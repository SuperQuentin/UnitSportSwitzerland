# Local release: `tools/release.sh`

- Manual only: there is no push hook, nothing releases on its own. Run `tools/release.sh` when you want a release. Docs/chore-only ranges release nothing.
- The build always runs in a temporary worktree of the released commit, so your working files are never touched.
- Releases are built and uploaded from your own machine, no GitHub Actions. Run from Git Bash on a synced `main`.
- VS Code tasks `release: dry run` and `release: build and publish` run it (Git Bash by full path on Windows).
- `tools/release.sh --dry-run` prints the next version and changelog; `tools/release.sh` exports "Windows Desktop", zips it and runs `gh release create vX.Y.Z`.
- Semver from gitmoji commits since the last `v*` tag: `BREAKING` anywhere = major; anything except fix/docs/chore/merge = minor; only `:bug:` `:ambulance:` `:recycle:` `:art:` `:white_check_mark:` = patch; only `:memo:` `:wrench:` merges = no release.
- Stamps `config/version` into `project.godot` for the export, then restores the file. Output in `test_output/release/`.
- Needs `gh` logged in, dotnet, Godot mono with export templates (`GODOT=` overrides the path, see `godot-exe`; `GODOT=godot` on WSL) and an `export_presets.cfg` ("Windows Desktop") in the repo root: it is gitignored, so the script copies it into its worktree. Zips with `zip` when present, else PowerShell.
- The export has no `terrain_chunks/`, so the released build uses the generated fallback world.
- Ships `bin/yt-dlp.exe`, `bin/qjs.exe` (QuickJS: without a JS runtime YouTube answers 403; passed via `BundledTools.YtDlpJsArgs`) and an LGPL `bin/ffmpeg.exe` (BtbN build, libvorbis included) next to the exe, downloaded once into `test_output/release/tools/` (delete to refresh; yt-dlp goes stale, `bin/yt-dlp.exe -U` updates it). `BundledTools.Resolve` (`src/Core`) prefers `bin/`, else PATH (Linux servers).
- First release on a new machine: install the mono export templates (Editor > Manage Export Templates, or unzip `Godot_v4.7.1-stable_mono_export_templates.tpz` from godot-builds so `windows_*` and `version.txt` land in `%APPDATA%/Godot/export_templates/4.7.1.stable.mono/`), and create a "Windows Desktop" preset: same `exclude_filter` as the Linux one in `tools/deploy-linux.sh`, `script_export_mode=2`, `application/modify_resources=false` (else the export needs rcedit).
