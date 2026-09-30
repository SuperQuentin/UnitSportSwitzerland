# Linux server deploy: `tools/deploy-linux.sh`

- One Git Bash command builds the Linux x86_64 **dedicated server** and deploys it to a Debian/Ubuntu host over SSH:
  `tools/deploy-linux.sh` (everything), or pick steps: `--setup`, `--build` (export + upload), `--chunks`, `--restart`;
  `--check` reports and changes nothing; `--dry-run` prints every change instead of making it; `--no-chunks` skips the tiles.
- **One-time setup:** `cp tools/deploy.env.example tools/deploy.env` (gitignored) and set `DEPLOY_HOST=user@host`; env vars override
  the file. Key auth is required: `ssh-keygen` once, then `ssh-copy-id user@host`. The user needs sudo (with a password is fine,
  the setup step asks for it through `ssh -t`). Linux export templates must be installed (Godot editor > Manage Export Templates).
- **Export preset:** `export_presets.cfg` is gitignored and per machine; the script appends a `"Linux Server"` preset
  (`platform="Linux"`, `dedicated_server=true`, the Windows preset's `exclude_filter`) when it is missing. Output: `build/linux/`.
- **.NET:** the local SDK must match the csproj `TargetFramework` (net8 -> SDK 8.x) and Godot must be the csproj's `Godot.NET.Sdk`
  version (4.7.1 mono); both checked first. The export bundles the .NET runtime (`data_*/libcoreclr.so`), so the host needs none;
  if an export ever comes out framework-dependent, setup installs `dotnet-runtime-<major>.0` instead.
- **Host setup** (`tools/deploy/remote-setup.sh`, run as root, idempotent, one OK/INSTALLED/FAIL line per item):
  - apt: `ffmpeg tmux avahi-daemon ufw cron curl ca-certificates tar unzip` + a `libicu` (.NET globalization).
  - `yt-dlp`: latest release binary in `/usr/local/bin` (apt's goes stale; updated with `-U` each run), plus `deno`, the JS runtime
    yt-dlp needs for YouTube. ffmpeg and yt-dlp are what server-side CD burning calls (`src/Audio/Cd/CdBurner.cs`).
  - mDNS: `/etc/avahi/services/unitsport.service` advertises `_unitsport._udp` on the game port, name `MDNS_NAME`, TXT
    `version=<git describe>`; clients list it in the main menu (`docs/notes/net/lan-discovery.md`).
  - ufw: the SSH port is allowed **before** `ufw --force enable` (no lockout), then `WEB_PORTS` (80 443) tcp, `GAME_PORT` (7777) udp,
    5353 udp.
  - cron: `@reboot $DEPLOY_DIR/start-server.sh` in the SSH user's crontab.
- **Layout on the host:** `$DEPLOY_DIR/releases/<stamp>/` (last 3 kept), `current` -> newest, `start-server.sh`, `server.log`,
  `cron.log`; tiles in `DEPLOY_CHUNKS_DIR` (default `$DEPLOY_DIR/terrain_chunks`, point it at a data volume), passed as
  `UNITSPORT_CHUNKS`. Server state (`admins.json`, bank, placed items, CDs...) lives in `~/.local/share/godot/app_userdata/`
  and is never touched by a deploy.
- **Server process:** a detached tmux session `unitsport`, so its stdin console still works:
  `ssh -t user@host tmux attach -t unitsport` (detach Ctrl-b d). A deploy kills the session (no graceful save exists) and
  restarts it, then waits for the UDP port (about 30 s) and prints the log tail.
- **Terrain chunks** (the real set is ~111 GB in ~246k files, one flat directory): the source is MapSetup's
  `terrain_location.json` `"chunks"`, else `terrain_chunks/`, else `CHUNKS_SRC=`; run from the main checkout (worktrees have no tiles).
  Files whose name, size or mtime differ are copied (`--checksum` compares md5 instead: reads every byte on both sides).
  Free space is checked first (new minus replaced bytes plus `SPACE_MARGIN_MB`). Copy is `rsync` when both ends have it,
  else a `tar | ssh tar` stream (keeps mtimes, so the next run is a no-op); `manifest.json` goes last. Server-only files are
  kept unless `--prune`. Lists land in `test_output/deploy/`. Listing 246k files from Git Bash takes minutes.
