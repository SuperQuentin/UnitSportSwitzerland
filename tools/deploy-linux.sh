#!/usr/bin/env bash
# Build the Linux x64 dedicated server and deploy it to a Debian/Ubuntu host over SSH.
# Usage: tools/deploy-linux.sh [--check] [--setup] [--build] [--chunks] [--nakama] [--restart] [--no-chunks] [--dry-run] [--checksum] [--prune]
#   no step flag = everything: setup, build + upload, chunk sync, Nakama (when NAKAMA=1), restart.  --check = report only.
# Config: tools/deploy.env (copy tools/deploy.env.example); env vars override it. Details: docs/notes/general/linux-deploy.md
# Needs (local, Git Bash or Linux): ssh with key auth to the host, tar, dotnet SDK, Godot mono + Linux export templates.
set -euo pipefail
cd "$(dirname "$0")/.."

usage() { sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
S_SETUP=0 S_BUILD=0 S_CHUNKS=0 S_NAKAMA=0 S_RESTART=0 CHECK=0 DRY=0 CHECKSUM=0 PRUNE=0 NOCHUNKS=0
for a in "$@"; do
  case $a in
    --check) CHECK=1 DRY=1 S_SETUP=1 S_CHUNKS=1 ;;
    --setup) S_SETUP=1 ;; --build) S_BUILD=1 ;; --chunks) S_CHUNKS=1 ;; --nakama) S_NAKAMA=1 ;; --restart) S_RESTART=1 ;;
    --no-chunks) NOCHUNKS=1 ;; --dry-run) DRY=1 ;; --checksum) CHECKSUM=1 ;; --prune) PRUNE=1 ;;
    -h|--help) usage ;; *) echo "Unknown option $a"; usage 1 ;;
  esac
done
[ $((S_SETUP + S_BUILD + S_CHUNKS + S_NAKAMA + S_RESTART)) = 0 ] && S_SETUP=1 S_BUILD=1 S_CHUNKS=1 S_NAKAMA=2 S_RESTART=1
[ $NOCHUNKS = 1 ] && S_CHUNKS=0

# --- config: tools/deploy.env, but variables already set in the environment win ---
VARS="DEPLOY_HOST DEPLOY_PORT_SSH DEPLOY_DIR DEPLOY_CHUNKS_DIR GAME_PORT WEB_PORTS MDNS_NAME SERVER_ARGS SPACE_MARGIN_MB CHUNKS_SRC GODOT TILES TILES_DOMAIN TILES_URL TILES_PRECOMPRESS WEB_MEDIA WEB_ANTENNAS"
VARS+=" LAYOUTS LAYOUTS_ADMINS LAYOUTS_PUBLIC_URL LAYOUTS_DB LAYOUTS_PORT LAYOUTS_TRUSTED_PROXIES SMTP_HOST SMTP_PORT SMTP_USER SMTP_PASS SMTP_FROM"
VARS+=" NAKAMA NAKAMA_DIR NAKAMA_PORT NAKAMA_URL ACCOUNTS"
for v in $VARS; do [ -n "${!v+x}" ] && eval "_keep_$v=\${$v}"; done
[ -f tools/deploy.env ] && . tools/deploy.env
for v in $VARS; do k="_keep_$v"; [ -n "${!k+x}" ] && eval "$v=\${$k}"; done
: "${DEPLOY_HOST:?set DEPLOY_HOST in tools/deploy.env (copy tools/deploy.env.example)}"
DEPLOY_PORT_SSH=${DEPLOY_PORT_SSH:-22} DEPLOY_DIR=${DEPLOY_DIR:-/opt/unitsport} GAME_PORT=${GAME_PORT:-7777}
WEB_PORTS=${WEB_PORTS-80 443} MDNS_NAME=${MDNS_NAME:-UnitSport Server} SERVER_ARGS=${SERVER_ARGS:-}
SPACE_MARGIN_MB=${SPACE_MARGIN_MB:-2048} CHUNKS_DIR=${DEPLOY_CHUNKS_DIR:-$DEPLOY_DIR/terrain_chunks}
# tiles over HTTP (#651): Caddy on the host serves the chunk directory, the game server names it to clients
TILES=${TILES:-1} TILES_DOMAIN=${TILES_DOMAIN:-} TILES_PRECOMPRESS=${TILES_PRECOMPRESS:-1} TILES_SITE=${TILES_DOMAIN:-:80}
if [ "$TILES" != 1 ]; then TILES_URL=
elif [ -z "${TILES_URL:-}" ]; then TILES_URL=$([ -n "$TILES_DOMAIN" ] && echo "https://$TILES_DOMAIN/tiles/" || echo "http://${DEPLOY_HOST#*@}/tiles/"); fi
WEB=$([ "$TILES" = 1 ] && echo "$DEPLOY_DIR/web" || true)   # the status page (#740) needs the tiles' Caddy
WEB_MEDIA=${WEB_MEDIA:-test_output/web-media}                # its clips and pictures, made by tools/web-media.sh
WEB_ANTENNAS=${WEB_ANTENNAS:-test_output/web-antennas/antennas-4g.json}   # the live map's 4G sites (#896), tools/deploy/antennas.py
# house layouts (#1186): the layout service behind the same Caddy at /api/, its data in $DEPLOY_DIR/layouts
LAYOUTS=$([ -n "$WEB" ] && echo "${LAYOUTS:-1}" || echo 0) LAYOUTS_PORT=${LAYOUTS_PORT:-5080} LAYOUTS_DIR=$DEPLOY_DIR/layouts
LAYOUTS_PUBLIC_URL=${LAYOUTS_PUBLIC_URL:-$([ -n "$TILES_DOMAIN" ] && echo "https://$TILES_DOMAIN" || echo "${TILES_URL%/tiles/}")}
LBUILD=build/layouts
# Nakama, the account server (#1121, docs/notes/net/nakama.md): a step of its own (--nakama), in "everything" when NAKAMA=1
NAKAMA=${NAKAMA:-0} NAKAMA_DIR=${NAKAMA_DIR:-$DEPLOY_DIR/nakama} NAKAMA_PORT=${NAKAMA_PORT:-7350}
# the game server's account step (#1123, net/accounts): off, optional or required, against the Nakama players sign in to
# behind the site's Caddy at /nakama/ when there is one (#1205: HTTPS, 7350 closed), else published on NAKAMA_PORT
NAKAMA_PUBLIC=$([ -n "$WEB" ] && { [ -n "$TILES_DOMAIN" ] && echo "https://$TILES_DOMAIN" || echo "${TILES_URL%/tiles/}"; } || true)
NAKAMA_BIND=$([ -n "$NAKAMA_PUBLIC" ] && echo 127.0.0.1 || echo 0.0.0.0)
NAKAMA_URL=${NAKAMA_URL:-$([ -n "$NAKAMA_PUBLIC" ] && echo "$NAKAMA_PUBLIC/nakama" || echo "http://${DEPLOY_HOST#*@}:$NAKAMA_PORT")} ACCOUNTS=${ACCOUNTS:-off}
case $ACCOUNTS in off|optional|required) ;; *) { echo "ERROR: ACCOUNTS=$ACCOUNTS: use off, optional or required" >&2; exit 1; } ;; esac
[ $S_NAKAMA = 2 ] && S_NAKAMA=$([ "$NAKAMA" = 1 ] && echo 1 || echo 0)
[ $CHECK = 1 ] && [ "$NAKAMA" = 1 ] && S_NAKAMA=1
GODOT=${GODOT:-'/c/ProgramData/chocolatey/lib/godot-mono/tools/godot_v4.7.2-stable_mono_win64/godot_v4.7.2-stable_mono_win64_console.exe'}
OUT=test_output/deploy; mkdir -p "$OUT"
BUILD=build/linux; BIN=UnitSportSwitzerland.x86_64
GODOT_VER=$(sed -n 's/.*Godot\.NET\.Sdk\/\([0-9.]*\).*/\1/p' UnitSportSwitzerland.csproj)
DOTNET_MAJOR=$(sed -n 's/.*<TargetFramework>net\([0-9]*\)\..*/\1/p' UnitSportSwitzerland.csproj | head -1)
VERSION=$(git describe --tags --always --dirty 2>/dev/null || echo dev)

say()  { printf '\n== %s\n' "$*"; }
die()  { echo "ERROR: $*" >&2; exit 1; }
qd()   { printf '%q' "$1"; }                         # quote one value for the remote shell
SSH=(ssh -p "$DEPLOY_PORT_SSH" -o ServerAliveInterval=30)
rq()   { "${SSH[@]}" "$DEPLOY_HOST" "$@"; }          # remote read-only query: runs even with --dry-run
rx()   { if [ $DRY = 1 ]; then echo "  [dry] remote: $*"; else rq "$@"; fi; }
avail(){ rq "d=$(qd "$1"); while [ ! -d \"\$d\" ]; do d=\$(dirname \"\$d\"); done; df -B1 --output=avail \"\$d\" | tail -1" | tr -dc 0-9; }
human(){ awk -v b="$1" 'BEGIN{split("B KB MB GB TB",u);i=1;while(b>=1024&&i<5){b/=1024;i++};printf "%.1f %s",b,u[i]}'; }

# --- 1. preflight ---------------------------------------------------------------
say "Preflight (local)"
for t in ssh tar find awk sort comm; do command -v $t >/dev/null || die "'$t' not found"; done
if [ $S_BUILD = 1 ]; then
  dotnet --list-sdks 2>/dev/null | grep -q "^$DOTNET_MAJOR\." || die ".NET $DOTNET_MAJOR SDK not installed (csproj targets net$DOTNET_MAJOR.0)"
  echo "  dotnet SDK $DOTNET_MAJOR    OK"
  [ -x "$GODOT" ] || command -v "$GODOT" >/dev/null || die "Godot not found at '$GODOT' (set GODOT=, see docs/notes/general/godot-exe.md)"
  gv=$("$GODOT" --version 2>/dev/null | tr -d '\r')
  case $gv in "$GODOT_VER".stable.mono*) echo "  Godot $gv    OK" ;; *) die "Godot is '$gv', the project needs $GODOT_VER.stable.mono" ;; esac
  tdir="${APPDATA:-$HOME/.local/share}/Godot/export_templates/$GODOT_VER.stable.mono"
  [ -d "$tdir" ] || tdir="$HOME/.local/share/godot/export_templates/$GODOT_VER.stable.mono"
  [ -f "$tdir/linux_release.x86_64" ] || die "Linux export templates missing in $tdir (Godot editor > Editor > Manage Export Templates)"
  echo "  export templates $GODOT_VER.stable.mono linux_release.x86_64    OK"
fi
say "Preflight ($DEPLOY_HOST)"
"${SSH[@]}" -o BatchMode=yes -o ConnectTimeout=10 "$DEPLOY_HOST" true \
  || die "ssh $DEPLOY_HOST failed with key auth. Run: ssh-keygen (once), then ssh-copy-id -p $DEPLOY_PORT_SSH $DEPLOY_HOST"
echo "  ssh    OK ($(rq 'uname -srm; . /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-}"' | tr '\n' ' '))"
SUDO=sudo
if rq 'sudo -n true' 2>/dev/null; then SUDO_TTY=(); echo "  sudo   OK (passwordless)"
elif [ -t 0 ]; then SUDO_TTY=(-t); echo "  sudo   will ask for your password"
elif [ $DRY = 1 ]; then SUDO_TTY=(); SUDO=; echo "  sudo   needs a password and there is no terminal: reporting without root"
else die "sudo on $DEPLOY_HOST needs a password but there is no terminal to type it"; fi

# --- 2. build (Linux x64, dedicated server) -------------------------------------------
ensure_preset() { # append a "Linux Server" preset to the (gitignored, per-machine) export_presets.cfg
  local f=export_presets.cfg n excl
  grep -q '^name="Linux Server"' "$f" 2>/dev/null && return
  n=$(grep -c '^\[preset\.[0-9]*\]$' "$f" 2>/dev/null || true); n=${n:-0}
  excl=$(sed -n 's/^exclude_filter="\(.*\)"$/\1/p' "$f" 2>/dev/null | head -1 || true)
  : "${excl:=assets/audio/*, terrain_chunks/*, terrain_chunks_png/*, terrain_chunks_temp/*, ressources/*, roadgen_out/*, video/*, graphify-out/*, tools/*}"
  echo "  adding preset.$n \"Linux Server\" to $f"
  [ -s "$f" ] && echo >> "$f"
  cat >> "$f" <<EOF
[preset.$n]

name="Linux Server"
platform="Linux"
runnable=false
dedicated_server=true
custom_features=""
export_filter="all_resources"
include_filter=""
exclude_filter="$excl"
export_path="$BUILD/$BIN"
patches=PackedStringArray()
encryption_include_filters=""
encryption_exclude_filters=""
seed=0
encrypt_pck=false
encrypt_directory=false
script_export_mode=2

[preset.$n.options]

custom_template/debug=""
custom_template/release=""
debug/export_console_wrapper=0
binary_format/embed_pck=false
texture_format/s3tc_bptc=true
texture_format/etc2_astc=false
binary_format/architecture="x86_64"
dotnet/include_scripts_content=false
dotnet/include_debug_symbols=false
dotnet/embed_build_outputs=false
EOF
}
if [ $S_BUILD = 1 ]; then
  say "Build $VERSION (Linux x86_64 dedicated server)"
  ensure_preset
  rm -rf "$BUILD"; mkdir -p "$BUILD"
  dotnet build UnitSportSwitzerland.csproj -c Release -v q -nologo
  "$GODOT" --headless --path . --import >/dev/null 2>&1 || true
  "$GODOT" --headless --path . --export-release "Linux Server" "$BUILD/$BIN"
  [ -f "$BUILD/$BIN" ] || die "export failed, no $BUILD/$BIN"
  echo "  $BUILD: $(human "$(du -sb "$BUILD" | cut -f1)")"
  if [ "$LAYOUTS" = 1 ]; then
    rm -rf "$LBUILD"
    dotnet publish tools/LayoutService -c Release -r linux-x64 --self-contained -v q -nologo -o "$LBUILD" >/dev/null || die "layout service publish failed"
    echo "  $LBUILD: $(human "$(du -sb "$LBUILD" | cut -f1)") (house layouts, #1186)"
  fi
fi

# .NET: Godot C# exports normally bundle the runtime (self-contained); only a framework-dependent export needs one on the host.
NEED_DOTNET=0
if [ -d "$BUILD" ]; then
  if ls "$BUILD"/data_*/libcoreclr.so >/dev/null 2>&1; then echo "  .NET $DOTNET_MAJOR runtime bundled in the export"
  else NEED_DOTNET=1; echo "  export is framework-dependent: the host needs the .NET $DOTNET_MAJOR runtime"; fi
fi

# --- 3. host setup: packages, .NET, dirs, mDNS, firewall, cron -----------------------------
if [ $S_SETUP = 1 ]; then
  say "Host setup$([ $DRY = 1 ] && echo ' (report only)')"
  rdir=$(tar -C tools/deploy -cf - remote-setup.sh get-vocal-model.sh unitsport.service.xml Caddyfile | rq 'd=$(mktemp -d) && tar -xf - -C "$d" && echo "$d"')
  envs="DRY=$DRY TARGET_USER=\$(id -un) DEPLOY_DIR=$(qd "$DEPLOY_DIR") CHUNKS_DIR=$(qd "$CHUNKS_DIR") GAME_PORT=$GAME_PORT"
  envs+=" SSH_PORT=$DEPLOY_PORT_SSH WEB_PORTS=$(qd "$WEB_PORTS") MDNS_NAME=$(qd "$MDNS_NAME") VERSION=$(qd "$VERSION")"
  envs+=" DOTNET_MAJOR=$DOTNET_MAJOR NEED_DOTNET=$NEED_DOTNET HERE=$rdir"
  envs+=" TILES=$TILES TILES_SITE=$(qd "$TILES_SITE") TILES_PRECOMPRESS=$TILES_PRECOMPRESS"
  envs+=" LAYOUTS_PORT=$LAYOUTS_PORT LAYOUTS_TRUSTED_PROXIES=$(qd "${LAYOUTS_TRUSTED_PROXIES:-}") NAKAMA_PORT=$NAKAMA_PORT"
  set +e
  "${SSH[@]}" "${SUDO_TTY[@]}" "$DEPLOY_HOST" "$SUDO env $envs bash $rdir/remote-setup.sh; rc=\$?; rm -rf $rdir; exit \$rc"
  rc=$?; set -e
  [ $rc = 0 ] || { [ $CHECK = 1 ] && echo "  (some items FAIL, see above)" || die "host setup failed (items marked FAIL above)"; }
fi

stopped=0
install_start() { # start-server.sh with this config baked in; cron and every restart run it
  sed -e "s|@DEPLOY_DIR@|$DEPLOY_DIR|; s|@CHUNKS_DIR@|$CHUNKS_DIR|; s|@GAME_PORT@|$GAME_PORT|" tools/deploy/start-server.sh \
    | awk -v a="$SERVER_ARGS${TILES_URL:+ --tiles-url $TILES_URL}$([ "$ACCOUNTS" != off ] && echo " --accounts $ACCOUNTS --nakama-url $NAKAMA_URL")${WEB:+ --status-file $WEB/status.json --livemap-file $WEB/players.json}$([ "$LAYOUTS" = 1 ] && echo " --house-layouts $LAYOUTS_DIR/house-layouts.json")" '{gsub(/@SERVER_ARGS@/, a)} 1' > "$OUT/start-server.sh"
  [ $DRY = 1 ] && return
  # the status page (#740), served by the same Caddy as the tiles; the server keeps status.json beside it
  [ -n "$WEB" ] && rq "mkdir -p $(qd "$WEB") && cat > $(qd "$WEB/index.html")" < tools/deploy/web/index.html
  # the live map (#896): the page, and OFCOM's 4G sites, fetched again when a week old
  if [ -n "$WEB" ]; then
    rq "cat > $(qd "$WEB/map.html")" < tools/deploy/web/map.html
    if [ ! -f "$WEB_ANTENNAS" ] || [ -n "$(find "$WEB_ANTENNAS" -mtime +7 2>/dev/null)" ]; then
      python tools/deploy/antennas.py "$WEB_ANTENNAS" || echo "  antennas: download failed, the map shows no coverage until the next deploy"
    fi
    [ -f "$WEB_ANTENNAS" ] && rq "cat > $(qd "$WEB/antennas-4g.json")" < "$WEB_ANTENNAS"
  fi
  if [ -n "$WEB" ] && [ -d "$WEB_MEDIA" ]; then  # sent only when the listing (name, size, mtime) differs: the trailer alone is ~35 MB
    list='find . -maxdepth 1 -type f -printf "%P %s %Ts\n" | LC_ALL=C sort'
    here=$(cd "$WEB_MEDIA" && eval "$list")
    there=$(rq "cd $(qd "$WEB/media") 2>/dev/null && $list" || true)
    if [ "$here" != "$there" ]; then
      tar -C "$WEB_MEDIA" -cf - . | rq "rm -rf $(qd "$WEB/media") && mkdir -p $(qd "$WEB/media") && tar -xf - -C $(qd "$WEB/media")"
      echo "  web media uploaded ($(du -sh "$WEB_MEDIA" | cut -f1))"
    fi
  elif [ -n "$WEB" ]; then echo "  no $WEB_MEDIA: the page shows its drawn background (make the clips with tools/web-media.sh)"; fi
  if [ "$LAYOUTS" = 1 ]; then install_layouts; else rq "rm -f $(qd "$DEPLOY_DIR/start-layouts.sh")"; fi
  rq "cat > $(qd "$DEPLOY_DIR/start-server.sh") && chmod 755 $(qd "$DEPLOY_DIR/start-server.sh")" < "$OUT/start-server.sh"
  # the in-game /update (#730): fetches a release while the server runs, start-server.sh switches to it
  rq "cat > $(qd "$DEPLOY_DIR/update-server.sh") && chmod 755 $(qd "$DEPLOY_DIR/update-server.sh")" < tools/deploy/update-server.sh
}
install_layouts() { # house layouts (#1186): the page, the settings (secrets: a 0600 file, never a command line), the start script
  rq "cat > $(qd "$WEB/houses.html")" < tools/deploy/web/houses.html
  [ -z "${LAYOUTS_ADMINS:-}" ] && echo "  LAYOUTS_ADMINS is empty: nobody can approve a house layout (set it in tools/deploy.env)"
  [ -z "${SMTP_HOST:-}" ] && echo "  SMTP_HOST is empty: login links go to $LAYOUTS_DIR/layouts.log instead of an email"
  { for v in LAYOUTS_DB SMTP_HOST SMTP_PORT SMTP_USER SMTP_PASS SMTP_FROM; do printf '%s=%q\n' "$v" "${!v:-}"; done
    printf '%s=%q\n' ADMIN_EMAILS "${LAYOUTS_ADMINS:-}" PUBLIC_URL "$LAYOUTS_PUBLIC_URL" LAYOUTS_URLS "http://127.0.0.1:$LAYOUTS_PORT" \
      LAYOUTS_DATA "$LAYOUTS_DIR" LAYOUTS_CHUNKS "$CHUNKS_DIR" LAYOUTS_EXPORT "$LAYOUTS_DIR/house-layouts.json"
  } | rq "mkdir -p $(qd "$LAYOUTS_DIR") && umask 077 && cat > $(qd "$LAYOUTS_DIR/layouts.env")"
  sed -e "s|@DEPLOY_DIR@|$DEPLOY_DIR|" tools/deploy/start-layouts.sh | rq "cat > $(qd "$DEPLOY_DIR/start-layouts.sh") && chmod 755 $(qd "$DEPLOY_DIR/start-layouts.sh")"
}
upload_layouts() { # the published service: a new app/ dir, then the session restarted on it
  [ "$LAYOUTS" = 1 ] && [ -d "$LBUILD" ] || return 0
  if [ $DRY = 1 ]; then echo "  [dry] upload $LBUILD -> $LAYOUTS_DIR/app, restart the layout service"; return; fi
  tar -C "$LBUILD" -czf - . | rq "rm -rf $(qd "$LAYOUTS_DIR/app.new") && mkdir -p $(qd "$LAYOUTS_DIR/app.new") && tar -xzf - -C $(qd "$LAYOUTS_DIR/app.new") \
    && chmod 755 $(qd "$LAYOUTS_DIR/app.new/LayoutService") && { tmux kill-session -t unitsport-layouts 2>/dev/null || true; } \
    && rm -rf $(qd "$LAYOUTS_DIR/app") && mv $(qd "$LAYOUTS_DIR/app.new") $(qd "$LAYOUTS_DIR/app")" || die "layout service upload failed"
  echo "  $LAYOUTS_DIR/app (house layout service; start-server.sh starts it)"
}
stop_server() { [ $stopped = 1 ] && return; rx "tmux kill-session -t unitsport 2>/dev/null || true"; stopped=1; }

# --- 4. upload the build ------------------------------------------------------------------------
if [ $S_BUILD = 1 ]; then
  say "Upload to $DEPLOY_HOST:$DEPLOY_DIR"
  need=$(( $(du -sb "$BUILD" | cut -f1) + SPACE_MARGIN_MB * 1048576 )); free=$(avail "$DEPLOY_DIR")
  [ "$free" -ge "$need" ] || die "not enough space in $DEPLOY_DIR: need $(human $need) (build + margin), $(human "$free") free"
  stamp=$(date +%Y%m%d-%H%M%S); rel="$DEPLOY_DIR/releases/$stamp"
  stop_server
  if [ $DRY = 1 ]; then echo "  [dry] upload $BUILD -> $rel, link $DEPLOY_DIR/current, install start-server.sh"
  else
    # chmod: a tar made on Windows carries no exec bit for the ELF binary
    tar -C "$BUILD" -czf - . | rq "mkdir -p $(qd "$rel") && tar -xzf - -C $(qd "$rel") && chmod 755 $(qd "$rel/$BIN") && ln -sfn $(qd "$rel") $(qd "$DEPLOY_DIR/current") \
      && cd $(qd "$DEPLOY_DIR/releases") && ls -1dt */ | tail -n +4 | xargs -r rm -rf"
    echo "  $rel (current), older than the last 3 releases removed"
  fi
  upload_layouts
fi

# --- 5. terrain chunks: copy what differs, never delete unless --prune ------------------------------
if [ $S_CHUNKS = 1 ]; then
  if [ -z "${CHUNKS_SRC:-}" ]; then  # same lookup as the game: MapSetup's terrain_location.json, then terrain_chunks/
    CHUNKS_SRC=$(sed -n 's/.*"chunks"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' terrain_location.json 2>/dev/null | sed 's/\\\\/\\/g' | head -1) || true  # no terrain_location.json: sed fails, pipefail would exit
    [ -n "$CHUNKS_SRC" ] && command -v cygpath >/dev/null && CHUNKS_SRC=$(cygpath -u "$CHUNKS_SRC")
    : "${CHUNKS_SRC:=terrain_chunks}"
  fi
  say "Terrain chunks $CHUNKS_SRC -> $DEPLOY_HOST:$CHUNKS_DIR"
  [ -f "$CHUNKS_SRC/manifest.json" ] || die "no $CHUNKS_SRC/manifest.json (set CHUNKS_SRC=, or run from the main checkout)"
  L=$OUT/chunks.local.tsv R=$OUT/chunks.remote.tsv SEND=$OUT/chunks.send.txt EXTRA=$OUT/chunks.extra.txt
  # the .gz copies the host makes for HTTP (step 5b) are not ours to compare or prune
  list='find . -maxdepth 1 -type f ! -name "*.gz" -printf "%P\t%s\t%T@\n"'
  norm() { awk -F'\t' '{printf "%s\t%s\t%d\n", $1, $2, $3}' | LC_ALL=C sort; }
  echo "  listing local files..."; (cd "$CHUNKS_SRC" && eval "$list") | norm > "$L"
  echo "  listing remote files..."; rq "cd $(qd "$CHUNKS_DIR") 2>/dev/null && $list || true" | norm > "$R"
  if [ $CHECKSUM = 1 ]; then  # content compare: slow (reads every byte on both sides)
    sums() { awk '{n=$2; sub(/^\*/, "", n); print n "\t" $1}' | LC_ALL=C sort; }
    echo "  hashing local files..."; (cd "$CHUNKS_SRC" && find . -maxdepth 1 -type f -printf '%P\0' | xargs -0 md5sum) | sums > "$L.md5"
    echo "  hashing remote files..."; rq "cd $(qd "$CHUNKS_DIR") 2>/dev/null && find . -maxdepth 1 -type f ! -name '*.gz' -printf '%P\0' | xargs -0 -r md5sum || true" | sums > "$R.md5"
    LC_ALL=C comm -23 "$L.md5" "$R.md5" | cut -f1 > "$SEND"
  else  # name + size + mtime (tar keeps mtimes, so a synced file matches on the next run)
    LC_ALL=C comm -23 "$L" "$R" | cut -f1 > "$SEND"
  fi
  LC_ALL=C comm -13 <(cut -f1 "$L") <(cut -f1 "$R") > "$EXTRA"
  # manifest.json goes last so the server never sees a manifest pointing at tiles still missing
  if grep -qx manifest.json "$SEND"; then grep -vx manifest.json "$SEND" > "$SEND.tmp" || true; echo manifest.json >> "$SEND.tmp"; mv "$SEND.tmp" "$SEND"; fi
  read -r nbytes obytes < <(awk -F'\t' 'FILENAME==ARGV[1]{s[$1]=1;next} FILENAME==ARGV[2]{if($1 in s)o+=$2;next} ($1 in s){n+=$2} END{printf "%d %d\n",n,o}' "$SEND" "$R" "$L")
  count=$(wc -l < "$SEND" | tr -d ' ')
  echo "  local $(wc -l < "$L" | tr -d ' ') files, remote $(wc -l < "$R" | tr -d ' '), to copy $count ($(human "$nbytes")), only on server $(wc -l < "$EXTRA" | tr -d ' ')"
  if [ "$count" -gt 0 ]; then
    free=$(avail "$CHUNKS_DIR"); need=$(( nbytes - obytes + SPACE_MARGIN_MB * 1048576 ))
    [ "$free" -ge "$need" ] || die "not enough space for chunks: need $(human $need) (new - replaced + margin), $(human "$free") free in $CHUNKS_DIR"
    echo "  space OK: $(human "$free") free, $(human $need) needed"
    if [ $DRY = 1 ]; then echo "  [dry] copy $count files (list: $SEND)"
    else
      stop_server
      if command -v rsync >/dev/null && rq 'command -v rsync' >/dev/null; then
        rsync -a --info=progress2 --files-from="$SEND" -e "ssh -p $DEPLOY_PORT_SSH" "$CHUNKS_SRC/" "$DEPLOY_HOST:$CHUNKS_DIR/"
      else
        # one progress line per ~1 GB (tar records are 10 KB)
        tar -C "$CHUNKS_SRC" --checkpoint=100000 --checkpoint-action='echo=  %T' -cf - -T "$SEND" \
          | rq "mkdir -p $(qd "$CHUNKS_DIR") && tar -xf - -C $(qd "$CHUNKS_DIR")"
      fi
      echo "  copied $count files"
    fi
  fi
  if [ -s "$EXTRA" ]; then
    if [ $PRUNE = 1 ]; then rx "cd $(qd "$CHUNKS_DIR") && xargs -d '\n' rm -f" < "$EXTRA"; echo "  pruned $(wc -l < "$EXTRA" | tr -d ' ') server-only files"
    else echo "  kept server-only files (--prune deletes them; list: $EXTRA)"; fi
  fi
fi

# --- 5b. gzip copies for the HTTP mirror (#651), made on the host --------------------------------------
if [ $S_CHUNKS = 1 ] && [ "$TILES" = 1 ] && [ "$TILES_PRECOMPRESS" = 1 ]; then
  say "Gzip copies for HTTP in $CHUNKS_DIR"
  if [ $DRY = 1 ]; then echo "  [dry] gzip -k every tile file whose .gz is missing or older (tools/deploy/gzip-tiles.sh)"
  else echo "  $(rq "bash -s -- $(qd "$CHUNKS_DIR")" < tools/deploy/gzip-tiles.sh) files compressed"; fi
fi

# --- 5c. Nakama, the account server (#1121): Docker + compose on the host, the stack in $NAKAMA_DIR -----------
# Its .env (keys, Postgres password) is made on the host by up.sh the first time and never sent or overwritten:
# a new encryption key would log every player out, a new Postgres password lock Nakama out of its volume.
if [ $S_NAKAMA = 1 ]; then
  say "Nakama ($NAKAMA_DIR, API port $NAKAMA_PORT)$([ $DRY = 1 ] && echo ' (report only)')"
  if rq 'docker compose version' >/dev/null 2>&1 || rq "$SUDO -n docker compose version" >/dev/null 2>&1; then
    echo "  docker compose    OK ($(rq "$SUDO docker compose version --short 2>/dev/null || docker compose version --short" | tr -d '
'))"
  elif [ $DRY = 1 ]; then echo "  docker compose    MISSING (a deploy installs docker.io and the compose v2 plugin from apt)"
  else
    echo "  installing Docker and the compose plugin (apt)"
    "${SSH[@]}" "${SUDO_TTY[@]}" "$DEPLOY_HOST" "$SUDO apt-get update -qq && { $SUDO DEBIAN_FRONTEND=noninteractive apt-get install -y -qq docker.io docker-compose-v2         || $SUDO DEBIAN_FRONTEND=noninteractive apt-get install -y -qq docker.io docker-compose-plugin         || $SUDO DEBIAN_FRONTEND=noninteractive apt-get install -y -qq docker.io docker-compose; } && $SUDO systemctl enable --now docker" >/dev/null
    rq "$SUDO -n docker compose version" >/dev/null 2>&1 || "${SSH[@]}" "${SUDO_TTY[@]}" "$DEPLOY_HOST" "$SUDO docker compose version" >/dev/null 2>&1       || die "no 'docker compose' (v2) on $DEPLOY_HOST after apt: install Docker's own packages (https://docs.docker.com/engine/install/), then run --nakama again"
  fi
  if [ $DRY = 1 ]; then
    echo "  [dry] upload tools/nakama/ (without .env) -> $NAKAMA_DIR, up.sh up on $NAKAMA_BIND:$NAKAMA_PORT, $([ "$NAKAMA_BIND" = 127.0.0.1 ] && echo "ufw closes $NAKAMA_PORT/tcp; Caddy serves $NAKAMA_URL" || echo "ufw allow $NAKAMA_PORT/tcp")"
    rq "test -f $(qd "$NAKAMA_DIR/.env")" 2>/dev/null && echo "  .env    present (kept)" || echo "  .env    none yet (up.sh makes one with new keys)"
    rq "cd $(qd "$NAKAMA_DIR") 2>/dev/null && $SUDO -n docker compose ps --format '{{.Service}} {{.Status}}'" 2>/dev/null | sed 's/^/  | /' || true
  else
    tar -C tools/nakama --exclude=.env -cf - docker-compose.yml local.yml .env.example up.sh modules       | rq "mkdir -p $(qd "$NAKAMA_DIR") && tar -xf - -C $(qd "$NAKAMA_DIR")"
    # the port and the bind in .env (up.sh writes .env from .env.example the first time); .env is root's, mode 600
    "${SSH[@]}" "${SUDO_TTY[@]}" "$DEPLOY_HOST" "cd $(qd "$NAKAMA_DIR") && sed -i 's/^NAKAMA_PORT=.*/NAKAMA_PORT=$NAKAMA_PORT/; s/^NAKAMA_BIND=.*/NAKAMA_BIND=$NAKAMA_BIND/' .env.example \
      && { [ ! -f .env ] || { $SUDO sed -i 's/^NAKAMA_PORT=.*/NAKAMA_PORT=$NAKAMA_PORT/' .env; $SUDO grep -q '^NAKAMA_BIND=' .env         && $SUDO sed -i 's/^NAKAMA_BIND=.*/NAKAMA_BIND=$NAKAMA_BIND/' .env || echo 'NAKAMA_BIND=$NAKAMA_BIND' | $SUDO tee -a .env >/dev/null; }; }       && $SUDO bash up.sh up" | sed 's/^/  | /'
    rq "$SUDO -n chmod 600 $(qd "$NAKAMA_DIR/.env") 2>/dev/null || chmod 600 $(qd "$NAKAMA_DIR/.env")" || true
    # behind Caddy the port stays closed (and is closed again if an older deploy opened it); without Caddy it is opened
    if [ "$NAKAMA_BIND" = 127.0.0.1 ]; then
      rx "if command -v ufw >/dev/null && $SUDO -n ufw status 2>/dev/null | grep -q '$NAKAMA_PORT/tcp'; then $SUDO -n ufw delete allow $NAKAMA_PORT/tcp >/dev/null && echo '  ufw: $NAKAMA_PORT/tcp closed (Caddy serves it at /nakama/)'; fi" || true
    else
      rx "if command -v ufw >/dev/null && $SUDO -n ufw status 2>/dev/null | grep -q 'Status: active'; then $SUDO -n ufw allow $NAKAMA_PORT/tcp >/dev/null && echo '  ufw: $NAKAMA_PORT/tcp open'; fi" || true
    fi
    rq "curl -fsS http://127.0.0.1:$NAKAMA_PORT/healthcheck >/dev/null" && echo "  healthcheck    OK" || die "Nakama does not answer on port $NAKAMA_PORT (ssh in, then: cd $NAKAMA_DIR && $SUDO bash up.sh logs)"
    # through Caddy too, when the site is plain HTTP on this host (a domain's HTTPS is checked from outside)
    if [ "$NAKAMA_BIND" = 127.0.0.1 ] && [ -z "$TILES_DOMAIN" ]; then
      rq "curl -fsS http://127.0.0.1/nakama/healthcheck >/dev/null" && echo "  through Caddy (/nakama/)    OK" \
        || echo "  WARNING: Caddy does not serve /nakama/ yet: run tools/deploy-linux.sh --setup once (it installs the new Caddyfile)"
    fi
    key=$(rq "sed -n 's/^NAKAMA_SERVER_KEY=//p' $(qd "$NAKAMA_DIR/.env") 2>/dev/null || $SUDO -n sed -n 's/^NAKAMA_SERVER_KEY=//p' $(qd "$NAKAMA_DIR/.env")" | tr -d '
')
    echo "  account server for players: $NAKAMA_URL, server key ${key:-<in $NAKAMA_DIR/.env>}"
    echo "  official server of a build: put both in assets/account-server.json (url, serverKey) before releasing"
    echo "  admin console: ssh -L 7351:127.0.0.1:7351 -p $DEPLOY_PORT_SSH $DEPLOY_HOST, then http://127.0.0.1:7351 (login in $NAKAMA_DIR/.env)"
  fi
fi

# --- 6. (re)start --------------------------------------------------------------------------------------
if [ $S_RESTART = 1 ] || [ $stopped = 1 ]; then
  say "Start server"
  install_start
  # wait for the old process to let go of the port, or the "listening" check below sees it and not the new one
  rx "tmux kill-session -t unitsport 2>/dev/null; for i in \$(seq 30); do ss -Hlun 'sport = :$GAME_PORT' | grep -q . || break; sleep 0.5; done; $(qd "$DEPLOY_DIR/start-server.sh")"
  if [ $DRY = 0 ]; then
    printf '  waiting for UDP %s' "$GAME_PORT"
    for _ in $(seq 60); do
      rq "ss -Hlun 'sport = :$GAME_PORT' | grep -q ." && { echo " - listening"; break; }
      rq "tmux has-session -t unitsport 2>/dev/null" || { echo " - server exited"; break; }
      printf .; sleep 2
    done
    # this run only: everything after the last start marker
    rq "awk '/^=== start/{b=\"\"} {b=b \$0 \"\\n\"} END{printf \"%s\", b}' $(qd "$DEPLOY_DIR/server.log") | tail -n 15" | sed 's/^/  | /'
    echo "  console: ssh -t -p $DEPLOY_PORT_SSH $DEPLOY_HOST tmux attach -t unitsport   (detach: Ctrl-b d)"
  fi
fi
say "Done ($VERSION)"
