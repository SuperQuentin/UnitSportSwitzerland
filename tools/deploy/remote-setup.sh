#!/usr/bin/env bash
# Host setup for the dedicated server, run as root on the server by tools/deploy-linux.sh (never by hand needed).
# Idempotent: checks every requirement, installs what is missing, prints one line per item.
# Inputs (env): DRY TARGET_USER DEPLOY_DIR CHUNKS_DIR GAME_PORT SSH_PORT WEB_PORTS MDNS_NAME VERSION
#               DOTNET_MAJOR NEED_DOTNET HERE (dir holding unitsport.service.xml and Caddyfile)
#               TILES (1 = serve tiles over HTTP with Caddy) TILES_SITE (domain or :80) TILES_PRECOMPRESS
set -uo pipefail
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin   # ldconfig and ufw live in sbin
FAIL=0
ok()   { printf '  %-28s OK %s\n' "$1" "${2:-}"; }
inst() { printf '  %-28s %s %s\n' "$1" "$([ "$DRY" = 1 ] && echo 'MISSING, would install' || echo INSTALLED)" "${2:-}"; }
did()  { if [ "$DRY" = 1 ]; then printf '  %-28s WOULD SET %s\n' "$1" "${2:-}"; else ok "$@"; fi; }
bad()  { printf '  %-28s FAIL %s\n' "$1" "${2:-}"; FAIL=1; }
act()  { if [ "$DRY" = 1 ]; then echo "  [dry] $*"; else "$@"; fi; }
export DEBIAN_FRONTEND=noninteractive

# read in a subshell: os-release defines VERSION, which would clobber ours (the build's git describe)
ids=$(. /etc/os-release 2>/dev/null; echo " ${ID:-} ${ID_LIKE:-} ")
pretty=$(. /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-unknown}")
case $ids in
  *" debian "*|*" ubuntu "*) ok "os" "$pretty" ;;
  *) echo "Unsupported distro '$pretty': this tool only handles Debian/Ubuntu (apt, ufw, avahi)."; exit 1 ;;
esac
ARCH=$(uname -m)
[ "$ARCH" = x86_64 ] && ok "arch" "$ARCH" || bad "arch" "$ARCH (the export is linux x86_64)"

# --- apt packages ---------------------------------------------------------
icu=$(ldconfig -p | grep -q 'libicuuc\.so' && echo "" || apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -1)
want=(ffmpeg tmux avahi-daemon ufw cron curl ca-certificates tar unzip)
missing=()
for p in "${want[@]}"; do dpkg -s "$p" >/dev/null 2>&1 || missing+=("$p"); done
[ -n "$icu" ] && missing+=("$icu")
if [ ${#missing[@]} -gt 0 ]; then
  act apt-get update -qq && act apt-get install -y -qq "${missing[@]}" >/dev/null \
    && inst "apt" "${missing[*]}" || bad "apt" "could not install: ${missing[*]}"
else
  ok "apt" "${want[*]} libicu"
fi
command -v ffmpeg >/dev/null && ffmpeg -version >/dev/null 2>&1 && ok "ffmpeg" "$(ffmpeg -version | head -1 | cut -d' ' -f3)" \
  || { [ "$DRY" = 1 ] || bad "ffmpeg" "not runnable (CD burning is refused without it)"; }

# --- yt-dlp: the apt build goes stale fast, YouTube needs the latest ------
YTDLP=/usr/local/bin/yt-dlp
if [ -x "$YTDLP" ]; then
  act "$YTDLP" -U -q >/dev/null 2>&1; ok "yt-dlp" "$("$YTDLP" --version 2>/dev/null)"
else
  act curl -fsSL -o "$YTDLP" https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux \
    && act chmod 755 "$YTDLP" && inst "yt-dlp" "$([ "$DRY" = 1 ] || "$YTDLP" --version)" || bad "yt-dlp" "download failed"
fi
# yt-dlp solves YouTube's JS challenges with an external runtime (deno by default); without it many links fail.
if command -v deno >/dev/null; then ok "deno (yt-dlp JS runtime)" "$(deno --version | head -1)"
else
  tmp=$(mktemp -d)
  act curl -fsSL -o "$tmp/deno.zip" https://github.com/denoland/deno/releases/latest/download/deno-x86_64-unknown-linux-gnu.zip \
    && act unzip -qo "$tmp/deno.zip" -d /usr/local/bin && inst "deno (yt-dlp JS runtime)" \
    || echo "  deno (yt-dlp JS runtime)     WARN not installed; some YouTube links may fail"
  rm -rf "$tmp"
fi

# --- .NET runtime (only when the export does not bundle it) ----------------
if [ "$NEED_DOTNET" = 1 ]; then
  if command -v dotnet >/dev/null && dotnet --list-runtimes | grep -q "Microsoft.NETCore.App $DOTNET_MAJOR\."; then
    ok "dotnet runtime" "$(dotnet --list-runtimes | grep "Microsoft.NETCore.App $DOTNET_MAJOR\." | tail -1)"
  else
    act apt-get install -y -qq "dotnet-runtime-$DOTNET_MAJOR.0" >/dev/null && inst "dotnet runtime" "$DOTNET_MAJOR.0" \
      || bad "dotnet runtime" "install dotnet-runtime-$DOTNET_MAJOR.0 (Microsoft package feed: https://learn.microsoft.com/dotnet/core/install/linux)"
  fi
else
  ok "dotnet runtime" "bundled in the export (.NET $DOTNET_MAJOR), none needed on the host"
fi

# --- directories ------------------------------------------------------------
act mkdir -p "$DEPLOY_DIR/releases" "$CHUNKS_DIR" && act chown "$TARGET_USER": "$DEPLOY_DIR" "$DEPLOY_DIR/releases" "$CHUNKS_DIR" \
  && did "dirs" "$DEPLOY_DIR, $CHUNKS_DIR (owner $TARGET_USER)" || bad "dirs"

# --- mDNS -------------------------------------------------------------------
esc() { sed 's/&/\&amp;/g; s/</\&lt;/g; s/>/\&gt;/g' <<< "$1"; }
svc=/etc/avahi/services/unitsport.service
new=$(<"$HERE/unitsport.service.xml"); name=$(esc "$MDNS_NAME"); ver=$(esc "$VERSION")
new=${new//@MDNS_NAME@/"$name"}; new=${new//@GAME_PORT@/"$GAME_PORT"}; new=${new//@VERSION@/"$ver"}
if [ "$DRY" = 1 ]; then echo "  [dry] write $svc (_unitsport._udp port $GAME_PORT, name '$MDNS_NAME')"
elif [ "$(cat "$svc" 2>/dev/null)" != "$new" ]; then printf '%s\n' "$new" > "$svc"; fi
act systemctl enable --now avahi-daemon >/dev/null 2>&1; act systemctl reload-or-restart avahi-daemon
if [ "$DRY" = 1 ]; then did "mdns" "_unitsport._udp '$MDNS_NAME' port $GAME_PORT"
elif systemctl is-active -q avahi-daemon; then ok "mdns" "_unitsport._udp '$MDNS_NAME' port $GAME_PORT"
else bad "mdns" "avahi-daemon not running"; fi

# --- tiles over HTTP (#651): Caddy serves the chunk directory under /tiles/ ----------------
if [ "${TILES:-1}" = 1 ]; then
  for p in caddy acl; do
    if dpkg -s "$p" >/dev/null 2>&1; then ok "$p" "$(dpkg-query -W -f='${Version}' "$p")"
    else act apt-get install -y -qq "$p" >/dev/null && inst "$p" || bad "$p" "apt-get install $p failed"; fi
  done
  # pre-compressed .gz copies beside the tiles (made by the deploy), else gzip on the fly
  if [ "${TILES_PRECOMPRESS:-1}" = 1 ]; then serve=$'file_server {\n\t\t\tprecompressed gzip\n\t\t}'
  else serve=$'encode gzip\n\t\tfile_server'; fi
  new=$(<"$HERE/Caddyfile"); new=${new//@TILES_SITE@/"$TILES_SITE"}; new=${new//@CHUNKS_DIR@/"$CHUNKS_DIR"}; new=${new//@WEB_DIR@/"$DEPLOY_DIR/web"}; new=${new//@COMPRESS@/"$serve"}
  cf=/etc/caddy/Caddyfile
  if [ "$DRY" = 1 ]; then echo "  [dry] write $cf (site $TILES_SITE, /tiles/ -> $CHUNKS_DIR)"
  elif [ "$(cat "$cf" 2>/dev/null)" != "$new" ]; then printf '%s\n' "$new" > "$cf"; fi
  # caddy runs as its own user: let it through the path (a home directory is 0700) and read the tiles
  if [ "$DRY" != 1 ] && ! runuser -u caddy -- test -r "$CHUNKS_DIR/." 2>/dev/null; then
    d=$CHUNKS_DIR; while [ "$d" != / ]; do d=$(dirname "$d"); setfacl -m u:caddy:x "$d"; done
    setfacl -R -m u:caddy:rX "$CHUNKS_DIR" && setfacl -d -m u:caddy:rX "$CHUNKS_DIR"
  fi
  # the status page (#740): the deploy uploads index.html, the game server writes status.json
  web=$DEPLOY_DIR/web
  act mkdir -p "$web" && act chown "$TARGET_USER": "$web"
  if [ "$DRY" != 1 ] && ! runuser -u caddy -- test -r "$web/." 2>/dev/null; then
    d=$web; while [ "$d" != / ]; do d=$(dirname "$d"); setfacl -m u:caddy:x "$d"; done
    setfacl -R -m u:caddy:rX "$web" && setfacl -d -m u:caddy:rX "$web"
  fi
  act systemctl enable caddy >/dev/null 2>&1; act systemctl reload-or-restart caddy
  if [ "$DRY" = 1 ]; then did "tiles http" "caddy $TILES_SITE/tiles/ -> $CHUNKS_DIR"
  elif systemctl is-active -q caddy && runuser -u caddy -- test -r "$CHUNKS_DIR/."; then ok "tiles http" "caddy $TILES_SITE/tiles/ -> $CHUNKS_DIR"
  else bad "tiles http" "caddy not running or cannot read $CHUNKS_DIR (journalctl -u caddy)"; fi
  if [ "$DRY" = 1 ]; then did "web page" "caddy $TILES_SITE/ -> $web"
  elif runuser -u caddy -- test -r "$web/."; then ok "web page" "caddy $TILES_SITE/ -> $web"
  else bad "web page" "caddy cannot read $web"; fi
fi

# --- firewall: ssh first so enabling ufw never locks us out -----------------
act ufw allow "$SSH_PORT/tcp" comment 'ssh' >/dev/null
for p in $WEB_PORTS; do act ufw allow "$p/tcp" comment 'web' >/dev/null; done
act ufw allow "$GAME_PORT/udp" comment 'unitsport game' >/dev/null
act ufw allow "$((GAME_PORT + 1))/udp" comment 'unitsport status query' >/dev/null
act ufw allow 5353/udp comment 'mdns' >/dev/null
if act ufw --force enable >/dev/null; then did "firewall" "ssh $SSH_PORT/tcp, web ${WEB_PORTS// /,}/tcp, game $GAME_PORT/udp, status $((GAME_PORT + 1))/udp, mdns 5353/udp"
else bad "firewall" "ufw enable failed (no iptables/nftables in this host?)"; fi

# --- autostart ----------------------------------------------------------------
act systemctl enable --now cron >/dev/null 2>&1
line="@reboot $DEPLOY_DIR/start-server.sh >> $DEPLOY_DIR/cron.log 2>&1"
cronu=(-u "$TARGET_USER"); [ "$(id -un)" = "$TARGET_USER" ] && cronu=()   # -u needs root, even for yourself
if crontab "${cronu[@]}" -l 2>/dev/null | grep -qxF "$line"; then ok "cron" "$line"
else
  if [ "$DRY" = 1 ]; then echo "  [dry] crontab -u $TARGET_USER: $line"
  else { crontab -u "$TARGET_USER" -l 2>/dev/null | grep -v 'start-server.sh'; echo "$line"; } | crontab -u "$TARGET_USER" - && inst "cron" "$line" || bad "cron"; fi
fi

exit $FAIL
