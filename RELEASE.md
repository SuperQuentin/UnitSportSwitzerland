# How to release

Releases are built on your own machine and uploaded to GitHub. Nothing releases automatically: no push hook,
no GitHub Actions. One release ships three downloads:

| Platform | Asset |
|---|---|
| Windows | `UnitSportSwitzerland-vX.Y.Z-windows.zip` |
| Linux x86_64 | `UnitSportSwitzerland-vX.Y.Z-linux-x86_64.tar.gz` |
| macOS (universal `.app`) | `UnitSportSwitzerland-vX.Y.Z-macos.tar.gz` |

`tools/release.sh` does every step below in one run. This file explains how to prepare a machine, how to run the
script, and how to do the same release by hand when the script cannot be used.

Background and gotchas: [`docs/notes/general/local-release.md`](docs/notes/general/local-release.md).
Deploying the dedicated Linux server is a separate step: [`docs/notes/general/linux-deploy.md`](docs/notes/general/linux-deploy.md).

## 1. One-time machine setup

1. **Tools**: Git Bash (Windows) or WSL, `gh` logged in (`gh auth login`), the .NET SDK that matches the csproj
   `TargetFramework`, `curl`, `unzip`, `tar`, `xz`, and `zip` (PowerShell `Compress-Archive` is used when `zip` is missing).
2. **Godot 4.7.1 mono**, the same version as the csproj's `Godot.NET.Sdk`. The script uses the Chocolatey install path by
   default; override it with `GODOT=/path/to/godot_console.exe` (on WSL: `GODOT=godot`). See
   [`docs/notes/general/godot-exe.md`](docs/notes/general/godot-exe.md).
3. **Export templates** (mono, 4.7.1): Godot editor > *Editor > Manage Export Templates* > Download, or unzip
   `Godot_v4.7.1-stable_mono_export_templates.tpz` so that `version.txt` and the `windows_*`, `linux_*` and `macos.zip`
   templates land in `%APPDATA%/Godot/export_templates/4.7.1.stable.mono/`.
4. **`export_presets.cfg`** in the repo root. It is gitignored (one per machine). Create a **"Windows Desktop"** preset in
   *Project > Export* with:
   - the same `exclude_filter` as the Linux preset in `tools/deploy-linux.sh` (keeps data folders out of the build),
   - `script_export_mode=2`,
   - `application/modify_resources=false` (otherwise the export needs `rcedit`).

   The script adds the **"Linux"** and **"macOS"** presets itself when they are missing, copying that `exclude_filter`.

## 2. Release with the script (normal way)

From Git Bash, in the main checkout:

```bash
git checkout main
git pull origin main          # main must equal origin/main, the script refuses otherwise
tools/release.sh --dry-run    # prints the next version and the changelog, builds nothing
tools/release.sh              # builds the three platforms and publishes the GitHub release
```

VS Code has the same two commands as tasks: `release: dry run` and `release: build and publish`.

What happens:

- The **version** comes from the gitmoji commits since the last `v*` tag:
  - `BREAKING` anywhere in a commit message: **major**;
  - any commit other than fix/docs/chore/merge: **minor**;
  - only `:bug:` `:ambulance:` `:recycle:` `:art:` `:white_check_mark:`: **patch**;
  - only `:memo:` `:wrench:` and merges: **no release** (the script stops).
- The build runs in a temporary worktree of the released commit (`test_output/release/wt`), so your working files,
  even uncommitted ones, are never touched.
- The outputs (archives, `notes.md`) are in `test_output/release/`.
- The GitHub release `vX.Y.Z` is created on that commit, with the changelog as notes and the three archives attached.

## 3. Full release by hand

Use this when the script fails halfway or you need to redo one step. Commands are for Git Bash from the repo root;
`$GODOT` is the Godot console executable and `V` the new version without the `v`.

### 3.1 Pick the version and write the notes

```bash
git checkout main && git pull origin main && git fetch --tags
last=$(git describe --tags --abbrev=0 --match 'v[0-9]*')
git log --no-merges --format='%s' "$last..HEAD"    # read the commits, apply the semver rules from section 2
V=1.4.0                                            # example
```

Write `test_output/release/notes.md` with three sections, each commit subject as one bullet without its gitmoji:
`### Features and changes`, `### Fixes` (`:bug:` `:ambulance:`), `### Docs and maintenance`
(`:memo:` `:wrench:` `:recycle:` `:art:` `:white_check_mark:`), then a last line
`**Full diff:** https://github.com/SuperQuentin/UnitSportSwitzerland/compare/<last>...v$V`.

### 3.2 Make a clean worktree of the commit

```bash
SHA=$(git rev-parse HEAD)
git worktree add --detach test_output/release/wt "$SHA"
cp export_presets.cfg test_output/release/wt/      # gitignored, so the worktree lacks it
cd test_output/release/wt
```

Stamp the version into the exported game (only in the worktree, never commit it): in `project.godot`, under the
line `config/name=...`, add `config/version="1.4.0"`.

### 3.3 Build and export

```bash
dotnet build UnitSportSwitzerland.csproj -c Release
"$GODOT" --headless --path . --import
mkdir -p build/windows build/linux build/macos
"$GODOT" --headless --path . --export-release "Windows Desktop" build/windows/UnitSportSwitzerland.exe
"$GODOT" --headless --path . --export-release "Linux"           build/linux/UnitSportSwitzerland.x86_64
"$GODOT" --headless --path . --export-release "macOS"           build/macos.zip
unzip -q build/macos.zip -d build/macos
```

Check that each file exists: a failed export often still exits with code 0.

### 3.4 Add the bundled tools (`bin/`)

Each platform ships a `bin/` folder next to the executable with **yt-dlp**, **QuickJS-ng `qjs`** (yt-dlp needs a JS
runtime, else YouTube answers 403) and **ffmpeg** (CD burning, GPX video export). The script caches them in
`test_output/release/tools/<platform>/`; copy them from there, or download:

| Platform | Into | yt-dlp | qjs | ffmpeg |
|---|---|---|---|---|
| Windows | `build/windows/bin/` | `yt-dlp.exe` | `qjs-windows-x86_64.exe` renamed `qjs.exe` | BtbN `ffmpeg-master-latest-win64-lgpl.zip` > `bin/ffmpeg.exe` |
| Linux | `build/linux/bin/` | `yt-dlp_linux` renamed `yt-dlp` | `qjs-linux-x86_64` renamed `qjs` | BtbN `ffmpeg-master-latest-linux64-lgpl.tar.xz` > `bin/ffmpeg` |
| macOS | `build/macos/UnitSportSwitzerland.app/Contents/MacOS/bin/` | `yt-dlp_macos` renamed `yt-dlp` | `qjs-darwin-arm64` renamed `qjs` | martin-riedl.de macOS arm64 release `ffmpeg.zip` |

Sources: yt-dlp `https://github.com/yt-dlp/yt-dlp/releases/latest`, qjs
`https://github.com/quickjs-ng/quickjs/releases/latest`, BtbN `https://github.com/BtbN/FFmpeg-Builds/releases/tag/latest`.

### 3.5 Package

Windows, a plain zip of the folder's contents:

```bash
(cd build/windows && zip -qr "../../UnitSportSwitzerland-v$V-windows.zip" .)
```

Linux and macOS must be `tar.gz` built with **explicit Unix modes**, because NTFS keeps no executable bit. Everything
`a+rX,u+w`, then the executables re-added as `0755`:

```bash
# Linux
tar -cf linux.tar -C build/linux --owner=0 --group=0 --mode=a+rX,u+w \
    --exclude=UnitSportSwitzerland.x86_64 --exclude='bin/*' .
tar -rf linux.tar -C build/linux --owner=0 --group=0 --mode=0755 \
    UnitSportSwitzerland.x86_64 bin/yt-dlp bin/qjs bin/ffmpeg
gzip -c linux.tar > "UnitSportSwitzerland-v$V-linux-x86_64.tar.gz"

# macOS: every file in Contents/MacOS (the game binary and bin/*) is executable
tar -cf macos.tar -C build/macos --owner=0 --group=0 --mode=a+rX,u+w \
    --exclude='UnitSportSwitzerland.app/Contents/MacOS/*' .
tar -rf macos.tar -C build/macos --owner=0 --group=0 --mode=0755 \
    $(cd build/macos && find UnitSportSwitzerland.app/Contents/MacOS -type f)
gzip -c macos.tar > "UnitSportSwitzerland-v$V-macos.tar.gz"
```

### 3.6 Publish and clean up

```bash
gh release create "v$V" UnitSportSwitzerland-v$V-*.zip UnitSportSwitzerland-v$V-*.tar.gz \
   --target "$SHA" --title "v$V" --notes-file ../notes.md
cd ../../..
git worktree remove --force test_output/release/wt
```

`gh release create` also creates the `vX.Y.Z` tag on GitHub; run `git fetch --tags` so the next release starts from it.

## 4. After the release

- Download one archive from the release page and launch it once to check it starts.
- The released build has **no `terrain_chunks/`**: it plays the generated fallback world unless the player points it at tiles.
- **macOS players** run `xattr -cr UnitSportSwitzerland.app` once before the first launch: the build is ad-hoc signed,
  not notarized, and adding `bin/` breaks the bundle seal. On Intel Macs `qjs` and `ffmpeg` (arm64 only) are taken from
  `PATH` (Homebrew) instead.
- To update the public server: `tools/deploy-linux.sh` (see [`docs/notes/general/linux-deploy.md`](docs/notes/general/linux-deploy.md)).
- A broken release can be withdrawn with `gh release delete vX.Y.Z --cleanup-tag`, then fixed and released again.
