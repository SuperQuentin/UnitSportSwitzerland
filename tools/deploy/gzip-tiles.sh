#!/usr/bin/env bash
# Run on the host by tools/deploy-linux.sh (#651): gzip copies beside the tiles, so Caddy's
# `precompressed gzip` sends file.gz for file with no CPU per request. Usage: gzip-tiles.sh <chunk dir>
# gzip -k gives the copy its source's mtime, so a copy not older than its file is up to date and is
# skipped: a re-run only compresses what the last sync changed. .cover and .water deflate inside their
# format already. Prints how many files it compressed.
set -euo pipefail
cd "$1"
find . -maxdepth 1 -type f \( -name '*.terr' -o -name '*.terrc' -o -name '*.road' -o -name '*.trees' \
    -o -name '*.bldg' -o -name '*.holes' -o -name '*.json' -o -name '*.bin' \) -printf '%P\0' \
  | nice xargs -0 -r -P "$(nproc)" -n 256 sh -c '
      n=0
      for f; do
        [ -e "$f.gz" ] && [ ! "$f.gz" -ot "$f" ] && continue
        gzip -kf -6 "$f" && n=$((n + 1))
      done
      echo $n' _ \
  | awk '{ s += $1 } END { print s + 0 }'
# a .gz whose file is gone (deleted by --prune) would still be served
find . -maxdepth 1 -type f -name '*.gz' -printf '%P\n' | while IFS= read -r g; do [ -e "${g%.gz}" ] || rm -f "$g"; done
