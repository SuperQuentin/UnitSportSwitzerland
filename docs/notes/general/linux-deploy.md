# Linux server deploy: `tools/deploy-linux.sh`

- One Git Bash command builds the Linux x86_64 **dedicated server** and deploys it to a Debian/Ubuntu host over SSH:
  `tools/deploy-linux.sh` (everything), or pick steps: `--setup`, `--build` (export + upload), `--chunks`, `--leaves`, `--restart`;
  `--check` reports and changes nothing; `--dry-run` prints every change instead of making it; `--no-chunks`/`--no-leaves` skip
  the tiles or the leaves. The earth flip (D2, protocol 96): the runbook at the end.
- **One-time setup:** `cp tools/deploy.env.example tools/deploy.env` (gitignored) and set `DEPLOY_HOST=user@host`; env vars override
  the file. Key auth is required: `ssh-keygen` once, then `ssh-copy-id user@host`. The user needs sudo (with a password is fine,
  the setup step asks for it through `ssh -t`, so run it from a real terminal; `--check`/`--dry-run` without a terminal report without root). Linux export templates must be installed (Godot editor > Manage Export Templates).
- **Export preset:** `export_presets.cfg` is gitignored and per machine; the script appends a `"Linux Server"` preset
  (`platform="Linux"`, `dedicated_server=true`, the Windows preset's `exclude_filter`) when it is missing. Output: `build/linux/`.
- **.NET:** the local SDK must match the csproj `TargetFramework` (net8 -> SDK 8.x) and Godot must be the csproj's `Godot.NET.Sdk`
  version (4.7.2 mono); both checked first. The export bundles the .NET runtime (`data_*/libcoreclr.so`), so the host needs none;
  if an export ever comes out framework-dependent, setup installs `dotnet-runtime-<major>.0` instead.
- **Host setup** (`tools/deploy/remote-setup.sh`, run as root, idempotent, one OK/INSTALLED/FAIL line per item):
  - apt: `ffmpeg tmux avahi-daemon ufw cron curl ca-certificates tar unzip` + a `libicu` (.NET globalization).
  - `yt-dlp`: latest release binary in `/usr/local/bin` (apt's goes stale; updated with `-U` each run), plus `deno`, the JS runtime
    yt-dlp needs for YouTube. ffmpeg and yt-dlp are what server-side CD burning calls (`src/Audio/Cd/CdBurner.cs`).
  - mDNS: `/etc/avahi/services/unitsport.service` advertises `_unitsport._udp` on the game port, name `MDNS_NAME`, TXT
    `version=<git describe>`; clients list it on the Multiplayer screen (`docs/notes/net/lan-discovery.md`).
  - ufw: the SSH port is allowed **before** `ufw --force enable` (no lockout), then `WEB_PORTS` (80 443) tcp, `GAME_PORT` (7777) udp,
    5353 udp.
  - cron: `@reboot $DEPLOY_DIR/start-server.sh` in the SSH user's crontab.
- **Layout on the host:** `$DEPLOY_DIR/releases/<stamp>/` (last 3 kept), `current` -> newest, `start-server.sh`, `server.log`,
  `cron.log`; tiles in `DEPLOY_CHUNKS_DIR` (default `$DEPLOY_DIR/terrain_chunks`, point it at a data volume), passed as
  `UNITSPORT_CHUNKS`. Server state (`admins.json`, bank, placed items, CDs...) lives in `~/.local/share/godot/app_userdata/`
  and is never touched by a deploy.
- **Tiles over HTTP** (#651, `TILES=1` by default, `net/http-tiles`): setup installs `caddy` and `acl`. It writes
  `/etc/caddy/Caddyfile` from `tools/deploy/Caddyfile`, serving `/tiles/` = `DEPLOY_CHUNKS_DIR` with tile files only. The site is
  `TILES_DOMAIN` (HTTPS via Let's Encrypt, HTTP/2; DNS must point at the host and 80/443 must be open) or `:80`. An ACL lets
  the `caddy` user reach a chunk dir under a 0700 home. The chunk step then runs `tools/deploy/gzip-tiles.sh` on the host:
  a `.gz` beside each changed tile file, about +25% disk, so leave room. `TILES_PRECOMPRESS=0` gzips on the fly instead.
  The `.gz` files are left out of the compare and of `--prune`. The server starts with `--tiles-url`: `https://TILES_DOMAIN/tiles/`,
  else `http://<DEPLOY_HOST without user@>/tiles/`. Set `TILES_URL` when `DEPLOY_HOST` is an ssh alias, or for a CDN.
  Check: `curl -sI -H 'Accept-Encoding: gzip' <url>chunk_2593_1119.terr` shows `Content-Encoding: gzip`.
- **Status page** (#740, `net/status-page`): the same Caddy serves `$DEPLOY_DIR/web` at `/`: `index.html` uploaded on
  every start install, `status.json` written by the server (`--status-file`). Players, names, version and the latest
  release's downloads at `http(s)://<host>/`. Needs `TILES=1`; a host set up before it needs `--setup` once.
- **Server process:** a detached tmux session `unitsport`, so its stdin console still works:
  `ssh -t user@host tmux attach -t unitsport` (detach Ctrl-b d). A deploy kills the session (no graceful save exists) and
  restarts it, then waits for the UDP port (about 30 s) and prints the log tail. **Ctrl-d in the console ends its stdin**:
  the server keeps running but reads no more commands until a restart. Inside the session `start-server.sh run`
  loops so the in-game `/update` can restart on a new release (`net/server-update`, `update-server.sh` installed beside it),
  and so the dashboard's Restart starts the same build again (the `restart-requested` marker, #1211). It puts
  `$DEPLOY_DIR/server-args.local` (the dashboard's startup arguments, never written by a deploy) first on the command
  line. Every tmux lookup is exact (`-t =unitsport`): a bare name is a prefix, and `unitsport` matched `unitsport-layouts`.
- **The dashboard's Server and Console tabs** (#1211, `net/web-dashboard`): `DASHBOARD=1` (default with a web site) starts
  the server with `--admin-http ADMIN_HTTP_PORT` (7790, loopback) and the Nakama on the host; Caddy serves it at
  `/server-api/` (a host set up before it needs `--setup` once).
- **Keeping it current** (#1088): a release-stamped server installs newer releases by itself once nobody plays
  (`net/server-update`; a `--build` deploy has no version and stays as deployed). When your game is newer than
  the server, its console (`net/console-link`) still joins it for the chat, with your admin rights there.
- **Nakama, the account server** (#1121, `net/nakama`): this is the `--nakama` step. It is part of a full deploy only
  with `NAKAMA=1` in `deploy.env`, which is off by default.
  - It installs Docker and the compose v2 plugin from apt when they are missing (`docker.io` plus `docker-compose-v2`,
    else `docker-compose-plugin`, else `docker-compose`). If none of them gives `docker compose`, it stops and points
    to Docker's install page.
  - It uploads `tools/nakama/` **without `.env`** to `$NAKAMA_DIR` (default `$DEPLOY_DIR/nakama`), then runs
    `up.sh up` as root.
  - The first run makes the host's `.env` (random keys, mode 600). Every later deploy keeps it: a new encryption key
    would log everyone out, and a new Postgres password would lock Nakama out of its volume.
  - **Behind Caddy** (#1205, a host with `TILES=1`): Nakama listens on 127.0.0.1 only and Caddy serves it at
    `https://<site>/nakama/`. The step closes `NAKAMA_PORT/tcp` (7350) in ufw if an older deploy opened it, then checks
    `/healthcheck` directly and through Caddy. A host set up before #1205 needs `--setup` once, for the new Caddyfile.
    `NAKAMA_URL` defaults to that public address. Without Caddy (`TILES=0`), 7350 is published and opened in ufw.
  - It prints what players need (`http://<host>:7350` and the server key) and the `ssh -L` tunnel for the console.
  - `--check` reports whether Docker, the `.env` and the containers are there.
  - Simulated locally by running the upload and `up.sh` twice: the keys stayed the same. A real host run was not
    done yet.
- **Terrain chunks** (the real set is ~111 GB in ~246k files, one flat directory): the source is MapSetup's
  `terrain_location.json` `"chunks"`, else `terrain_chunks/`, else `CHUNKS_SRC=`; run from the main checkout (worktrees have no tiles).
  Files whose name, size or mtime differ are copied (`--checksum` compares md5 instead: reads every byte on both sides).
  Free space is checked first (new minus replaced bytes plus `SPACE_MARGIN_MB`). Copy is `rsync` when both ends have it,
  else a `tar | ssh tar` stream (keeps mtimes, so the next run is a no-op); `manifest.json` goes last. Server-only files are
  kept unless `--prune`. Lists land in `test_output/deploy/`. Listing 246k files from Git Bash takes minutes.
- **French border tiles on a second disk** (Nov 2026): the game and Caddy read one flat directory and `/home` is full, so 42,744 French tiles
  (terrain only, every ring out to 76 km from the served set, from the NAS's `terrain_chunks_france`) live on an ext4 SSD at
  `/srv/unitsport-tiles/france` (`.terr`, `.terrc` and their `.gz`, mode 644: the NAS's 750 would make Caddy refuse them) and are symlinked into
  `DEPLOY_CHUNKS_DIR`. Listings use `-type f`, so `--chunks`, `--prune` and `gzip-tiles.sh` ignore the links. **But `manifest.json` and `horizon.bin` there are
  merged**: a `--chunks` deploy replaces them from the source and the French tiles drop out of the game. Merge again afterwards with
  `/srv/unitsport-tiles/index/merge_border.py <dir> 76` (`<dir>`: `swiss_manifest.json`, `swiss_horizon.bin` from the new source, `france_manifest.json`,
  `france_horizon.bin` from the NAS; it writes `merged_manifest.json`, `merged_horizon.bin`, `file_list.txt`), install both with fresh `.gz` copies, restart. The tiles the
  source already has win, so rebuild the links if it gained some. Pre-merge files: `index/before/`. The SSD is an old OCZ Vertex 2 (`smartctl -A /dev/sda`);
  it mounts `nofail`, so losing it costs the French tiles, not the boot; undo = restore `index/before/` and delete the symlinks (`find -type l`).
  These tiles were not aligned to the Swiss heights (dz 0, not -0.28 m; `tools/france-nas-run`).
- **SMTP check** (#1287, `net/house-layouts`): `tools/deploy-linux.sh --smtp-check you@example.ch` is a step of its own (no build, no other step): over SSH it loads `layouts/layouts.env` and runs `app/LayoutService --smtp-check`, which mails a test message (MailKit: port 465 implicit TLS, 587 or other ports STARTTLS) and prints the exact failure; the password never leaves the host.
- **The earth world's leaves** (G2 D2, #1792): the `--leaves` step, part of a full deploy and of `--check`. It syncs
  `LEAVES_SRC` (`.cubl` bundles and `cube_manifest.json`) to `DEPLOY_LEAVES_DIR` the same way as the tiles: what differs
  is copied, after a free-space check, with `cube_manifest.json` last, and nothing is deleted without `--prune`.
  - Where: `LEAVES_SRC` defaults to the chunk source's `_cube` sibling. Today it is B4's set,
    `D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube`: 16 files, 775 MB, 2,412 L14 leaves, Geneva to Riddes. It is
    the set every earth check and the playtest use (`D:/UnitSportSwitzerland/terrain_chunks_cube` does not exist). The
    server's own copy is built on the NAS (`tools/cube-nas-run`): `X:/UnitSportSwitzerland/terrain_chunks_cube`.
    `DEPLOY_LEAVES_DIR` defaults to `DEPLOY_CHUNKS_DIR`'s `_cube` sibling, where the game also looks without a flag.
  - The server starts with `--leaves <dir>` (`LEAVES=0` drops it), and with `--frame <FRAME>` only when `FRAME` is set.
  - Caddy needs nothing: leaves stream over ENet (`net/leaf-streaming`), never HTTP.
  - A flat server never opens the leaves, so the step copies them while it runs. A server whose log since its last start
    says `[frame] earth` holds their index, so it is stopped first and started again.
  - `--check` adds a "Server" block. It shows whether `current` is a release (it updates itself, `net/server-update`) or a
    deploy build (it stays as deployed); the running server's `[frame]` and leaf count; and the `--leaves`/`--frame` in
    `start-server.sh`.
- **Not the `cube_global` pyramid.** The fill outside the leaves must stand on the same base on the server and every
  client (`GameLeaves`: a client and the server make the same fill leaf). A release has only the shipped levels 0-2
  (`res://assets/globe`), on which `PyramidBase.Open` gives null, so the fill stands on the generator's relief.
  - Measured with `--fillagree` at B4's south-west edge (20 fill points, 5 real): the shipped globe on both sides agreed
    to 0.000 m. The full pyramid on one side only was off by 20.5 m on the fill on average, 32.3 m at worst, whichever side had it.
  - So the server gets no `--globe`. The deploy preset includes `assets/globe/*`, as the CI one does, which is for the
    server's line of sight only.
  - The user's own PC finds `D:/UnitSportSwitzerland/cube_global` by itself, so on that PC the fill is ~20 m off from the server's
    outside the region. `--globe res://assets/globe` matches the server. The fix is #1656 (the server streams the pyramid).
- **What an earth client gets online** (D2 at `daf0751d3`, Nov 13 2026, one PC over loopback):
  - The setup: a server with the default frame, `--chunks` the full flat set, `--leaves` B4 and the shipped globe; a
    release-like client with no tiles, no leaves, an empty cache and the shipped globe, `--earthcheck` at the default
    spawn (2583250/1113250, Riddes).
  - The leaf index came first (42 KB, 2,412 leaves), then every leaf over ENet. Ground under the start came at 61.8 s,
    with 113 MB received. It had 170.6 MB after 3 km, held 457 leaf entries in its cache, and found 888 doors within 600 m.
  - It still streamed flat files: 112 `.terr`/`.terrc`, 69 `.road`, 45 `.bldg`, 12 `.cover`, `horizon.bin`,
    `places.json` and `landings.json`. They go through Caddy when `--tiles-url` is set, so **the flat tiles stay deployed
    until D3**. The earth server also reads its places, landings and airports from the chunk dir.
  - The same server with **no leaves** (none deployed, no `--leaves`, no `_cube` sibling) did not fail. It logged
    `0 leaves`, and the client logged "the server holds no leaves": generated fill everywhere, ground at 12.1 s.
  - Not measured over a real internet link. ENet is capped per client (`--stream-bandwidth`), so a first join at
    Riddes takes about a minute on the fill before the real leaves replace it in place.
- **Checked without a host:** the script ran against WSL Ubuntu standing in for the host (a stub `ssh` that runs the
  command there). The runs: `--check` on an empty host; `--leaves` with the full 775 MB B4 set (byte-identical md5, a second
  run copies 0); `--prune`; a flat server left running and an earth server stopped and restarted; `--restart --dry-run`
  with `FRAME`. This machine has no `tools/deploy.env` and no SSH key, so nothing ran against the real host.

- **Gotchas seen on the first real deploy (Debian 13):** the root partition was 19 GB, so the space check refused the tiles until
  `DEPLOY_CHUNKS_DIR` pointed at `/home` (check `df -h` on the host first). `/etc/os-release` defines `VERSION`, so the setup
  script reads it in a subshell or the mDNS TXT would advertise "13 (trixie)". A server started before the tiles arrive exits
  with `no terrain data` - expected; `--chunks --restart` brings it up.

### Runbook: the D2 flip, protocol 95 -> 96 (#1727)

The release has no terrain: CI has no data, so a player's earth ground is the server's leaves (streamed) and
the generated fill. A 95 client and a 96 server refuse each other, both ways: "This server runs a newer (older) version of the
game (protocol 96, yours 95)". A refused newer client also reads that the server updates itself, and the refusal makes a
release server check for the new release at once. The mismatch lasts from D2's release to the server's restart on it.
The steps below keep it to the `/update` download (1-2 min).

**Before merging (any day; harmless to the 95 server):**
0. Build the leaves on the NAS (`tools/cube-nas-run`, #1794), from the repo on `main`:
   `tools/build-swiss-nas.sh cube check` (every source there, or what to fetch), `cube test` (Geneva, ~10-15 min), then
   `cube start` and `cube status` until `ALL STAGES DONE` (~2 h; the server's region by default: B4's 825 tiles). The
   output is `X:\UnitSportSwitzerland\terrain_chunks_cube` (`/mnt/Data/Documents/UnitSportSwitzerland/terrain_chunks_cube`).
1. In `tools/deploy.env`, set `LEAVES_SRC=X:/UnitSportSwitzerland/terrain_chunks_cube` (the NAS share; B4's
   `D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube` on the user's PC is the same region) and leave `FRAME` empty.
2. `tools/deploy-linux.sh --check`. It takes minutes, mostly listing the flat tiles. Read:
   - the leaves' "space OK";
   - the "Server" block: a release or a deploy build, `[frame] flat`.

   `/home` was full in Nov 2026. If the space check fails, set `DEPLOY_LEAVES_DIR=/srv/unitsport-tiles/leaves`
   (the SSD; 775 MB plus the 2 GB margin). That disk mounts `nofail`, so a missing SSD means a server on the fill alone.
3. `tools/deploy-linux.sh --leaves`. 775 MB at your upload speed: about 1 min at 100 Mbit/s, 5 min at 20. The 95 server keeps running.
4. When nobody plays (the status page): `tools/deploy-linux.sh --restart`, about 1 min. It installs `start-server.sh` with
   `--leaves <dir>`; the 95 server ignores it.
5. `tools/deploy-linux.sh --check` again: `start-server.sh: --leaves ...` and `cube_manifest.json present`.

**The switch:**

6. Merge #1727. The release waits until an hour after the latest one, then 3 quiet minutes, then ~5-15 min of build.
   To pick the moment, cancel the holding run and dispatch it with `-f min_gap_minutes=0 -f quiet_minutes=0`
   (`release-on-merge`). Watch it with `gh run list -w release.yml --limit 2` and
   `gh release view --json tagName,publishedAt`.
7. Once the release is published, an admin types `/update` in game, or `update` in the server console
   (`ssh -t <host> tmux attach -t unitsport`). It downloads the Linux archive (~185 MB) and kicks everyone with "update your
   game". The server restarts on D2 with the same `start-server.sh`, so `--leaves` comes along. This takes 1-2 min.
   - Without `/update`, a release server fetches the new release within 10 min (at once on a refused 96 client), but
     restarts only once nobody is connected.
   - A deploy build never updates itself: use `/update`, or `tools/deploy-linux.sh --build --restart` from `main` at the release.
8. Check:
   - `tools/deploy-linux.sh --check` shows `running: [frame] earth; [earth] 2412 leaves from <dir>` (B4's count; the NAS
     build of the same tiles should match: its manifest's `"key"` count);
   - a fresh client's log says `[stream] leaf index received: 2412 leaves`.

**Rollback:**
- **The leaves:** nothing to undo; they cost a 95 server nothing. To free the 775 MB, delete `DEPLOY_LEAVES_DIR`.
- **The server alone cannot go back to flat for players.** D2 clients play earth by default, and the handshake does not
  compare frames. `FRAME=flat` (then `--restart`) is only for a server whose clients also run `--frame flat`.
- **D2's own rollback** (G2 D plan §6): a PR setting the default back (protocol stays 96), merged and released, then
  `/update` on the server. About 20 min with a dispatched release, or up to 1.5 h with the hourly gap.
- **Back to 95:** `ln -sfn releases/v0.251.0 current` in `DEPLOY_DIR`, plus `--no-auto-update` in `server-args.local`,
  plus a restart. Every 96 client is then refused, so this is only for when D2's server itself is broken.
