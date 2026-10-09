#!/usr/bin/env bash
# Installed on the server as $DEPLOY_DIR/update-server.sh by tools/deploy-linux.sh; driven by the in-game /update (#730).
#   fetch <tag> <url>  the server (Net/ServerUpdater), while it still runs: download a release's Linux archive, unpack
#                      it into releases/<tag> and mark it pending. On a failure the last line is the reason shown in chat.
#   apply              start-server.sh, once the server quit: point current at the pending release, keep the last 3.
set -euo pipefail
DIR=$(cd "$(dirname "$0")" && pwd)
PENDING=$DIR/pending-update
BIN=UnitSportSwitzerland.x86_64

case ${1-} in
  fetch)
    tag=${2-} url=${3-}
    case $tag in ''|*/*|.*) echo "bad tag '$tag'"; exit 1;; esac
    rel=$DIR/releases/$tag part=$DIR/releases/$tag.part
    free_kb=$(df -Pk "$DIR" | awk 'NR==2{print $4}')
    [ "$free_kb" -gt 1048576 ] || { echo "less than 1 GB free in $DIR"; exit 1; }
    rm -rf "$part"; mkdir -p "$part"
    curl -fsSL --retry 3 --max-time 1800 "$url" | tar -xzf - -C "$part" || { rm -rf "$part"; echo "download or unpack failed"; exit 1; }
    [ -f "$part/$BIN" ] || { rm -rf "$part"; echo "no $BIN in the archive"; exit 1; }
    chmod 755 "$part/$BIN"
    rm -rf "$rel"; mv "$part" "$rel"
    echo "$tag" > "$PENDING"
    echo "$tag unpacked in $rel";;
  apply)
    tag=$(cat "$PENDING"); rm -f "$PENDING"
    [ -x "$DIR/releases/$tag/$BIN" ] || { echo "releases/$tag is missing"; exit 1; }
    ln -sfn "$DIR/releases/$tag" "$DIR/current"
    cd "$DIR/releases" && ls -1dt */ | tail -n +4 | xargs -r rm -rf
    echo "current -> releases/$tag";;
  *) echo "usage: $0 fetch <tag> <url> | apply" >&2; exit 2;;
esac
