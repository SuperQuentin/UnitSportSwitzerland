#!/usr/bin/env bash
# Rebuild the WHOLE Swiss map from the raw data on the TrueNAS box, in tile format v3 (--height-format wide,
# terrain/height-format-v3), into terrain_chunks_v3 so the current build (terrain_chunks) stays untouched.
# From Git Bash on Windows. Same runner as build-france.sh (self-contained linux-x64 publish, detached, resumable);
# the stages are in tools/lib/swiss-nas-chain.sh: terrain, frdem, dem, osm, features, airports, hash.
#
# Usage: tools/build-swiss-nas.sh <command> [args]
#   publish                 dotnet publish (self-contained linux-x64) and copy it to the NAS
#   start | resume          publish, then run the stages detached; finished stages are skipped
#                           FROM=<stage> start   redo that stage and every later one (e.g. FROM=features)
#   status                  process, stages done, current stage, RSS, problems, disk
#   stop                    SIGTERM, then SIGKILL after 60 s (every file is written atomically)
#   test [geneva|basel|E0,N0,E1,N1]   every stage, foreground, on a small box (LV95 km tile numbers, max
#                           exclusive) into terrain_chunks_v3_test/<name>: per-stage time, peak RSS, tiles, heights
#   log [N]                 last N lines of the run log (default 40)
# Env (all optional):
#   NAS=truenas_admin@TRUENAS   NAS_ROOT=/mnt/Data/Documents/UnitSportSwitzerland (= X:\UnitSportSwitzerland)
#   HEAP_GB=16      GC heap hard limit (31 GB box: the rest stays for the ZFS ARC)
#   JOBS=           threads (default nproc)      NICE=10      DEM_CACHE_MB=4096
#   FR_BOX=2494,1099,2566,1146   LV95 km box of the French DEM stage (as #939: Chablais, Geneva)
#   FR_DEPTS=       limit France to these departments (D074,...); default all that lie in the box
#   OTHER_BOX=auto  box of the DE/AT/IT stage (auto = what was downloaded)
#   NO_HASH=1       skip the final manifest-hash pass
#   FROM=           first stage to redo (terrain frdem dem osm features airports hash)
# The output has no foreign features (--france is not part of the current build either). Notes: docs/notes/tools/swiss-nas-run.md
set -euo pipefail
cd "$(dirname "$0")/.."

NAS=${NAS:-truenas_admin@TRUENAS}
NAS_ROOT=${NAS_ROOT:-/mnt/Data/Documents/UnitSportSwitzerland}
HEAP_GB=${HEAP_GB:-16}
NICE=${NICE:-10}
DEM_CACHE_MB=${DEM_CACHE_MB:-4096}
FR_BOX=${FR_BOX:-2494,1099,2566,1146}
OTHER_BOX=${OTHER_BOX:-auto}
BIN_DIR=$NAS_ROOT/bin/TerrainPreprocessor-linux-x64
OUT=$NAS_ROOT/terrain_chunks_v3
TEMP=${OUT}_temp
LOG=$NAS_ROOT/terrain_chunks_v3.log
PIDFILE=$NAS_ROOT/terrain_chunks_v3.pid
CHAIN=$NAS_ROOT/bin/swiss-nas-chain.sh
PUBLISH=test_output/build-swiss-nas/publish

. tools/lib/nas.sh

send_chain() { nas "cat > '$CHAIN'" < tools/lib/swiss-nas-chain.sh; }

# the environment the chain script reads, as `export` lines
chain_env() { # out temp frbox otherbox [indir]
  cat <<ENV
export OUT='$1' TEMP='$2' DATA='$NAS_ROOT/data' BIN='$BIN_DIR' NICE=$NICE DEM_CACHE_MB=$DEM_CACHE_MB
export FR_BOX='$3' OTHER_BOX='$4' FR_DEPTS='${FR_DEPTS:-}' FROM='${FROM:-}' NO_HASH='${NO_HASH:-}'
export JOBS=${JOBS:-\$(nproc)} ${5:+IN_DIR='$5'}
export DOTNET_GCHeapHardLimit=$(heap_hex)
ENV
}

start() {
  if alive; then echo "already running (pid $(nas cat "$PIDFILE")); use status / stop" >&2; exit 1; fi
  publish; send_chain
  echo "== starting on the NAS: heap limit ${HEAP_GB} GB, nice $NICE, from '${FROM:-first unfinished stage}', log $LOG"
  nas "bash -s" <<REMOTE
set -e
cd '$NAS_ROOT'
$(chain_env "$OUT" "$TEMP" "$FR_BOX" "$OTHER_BOX")
echo "=== run started \$(date '+%F %T') ===" >> '$LOG'
setsid nohup bash '$CHAIN' >> '$LOG' 2>&1 < /dev/null &
echo \$! > '$PIDFILE'
REMOTE
  sleep 3
  alive && echo "== running, pid $(nas cat "$PIDFILE")" || { echo "== died at once; last log lines:" >&2; nas tail -n 20 "$LOG" >&2; exit 1; }
}

status() {
  if alive; then
    local pid; pid=$(nas cat "$PIDFILE")
    echo "running: chain pid $pid"
    nas "for p in \$(pgrep -P $pid; echo $pid); do grep -E 'Name|VmRSS|VmHWM' /proc/\$p/status 2>/dev/null | tr -s ' \t' ' ' | tr '\n' ' '; echo; done; ps -o etime= -p $pid | awk '{print \"elapsed \" \$1}'"
  else
    echo "not running"
  fi
  nas "bash -s" <<REMOTE
cd '$NAS_ROOT'
echo '-- stages:'; grep -E '^=== .* (stage|ALL|run started|stopped)' '$LOG' 2>/dev/null | tail -n 8
echo "-- done markers: \$(ls '$TEMP' 2>/dev/null | grep '^stage_' | tr '\n' ' ')"
echo '-- last progress:'; grep -E '^\s+\[[0-9]+/[0-9]+\]|tiles/s' '$LOG' 2>/dev/null | tail -n 3 | cut -c1-200
echo "-- problems: \$(grep -cE '\[FAIL\]|Unhandled|Exception|FAILED' '$LOG' 2>/dev/null || true) lines (last:)"
grep -E '\[FAIL\]|Unhandled|Exception|FAILED' '$LOG' 2>/dev/null | tail -n 5 | cut -c1-200
echo "-- tiles on disk: \$(ls '$OUT' 2>/dev/null | grep -c '\.terr\$' || true) .terr"
df -h '$NAS_ROOT' | tail -n 1
REMOTE
}

stop() {
  if ! alive; then echo "not running"; return 0; fi
  local pid; pid=$(nas cat "$PIDFILE")
  echo "SIGTERM to $pid"
  nas "kill $pid; for i in \$(seq 60); do kill -0 $pid 2>/dev/null || exit 0; sleep 1; done; echo 'still alive: SIGKILL'; kill -9 -- -$pid"
  echo "stopped; start again to resume (finished stages and tiles are kept)"
}

test_run() {
  local area=${1:-geneva} box
  case $area in
    geneva) box=2499,1113,2505,1119 ;;   # Geneva town, the French side at Etrembieres/Veyrier
    basel)  box=2608,1265,2614,1270 ;;   # Basel, the German and French bank
    *,*,*,*) box=$area ;;
    *) echo "area: geneva | basel | E0,N0,E1,N1" >&2; exit 2 ;;
  esac
  local name=${area//,/_} base=$NAS_ROOT/terrain_chunks_v3_test
  local out=$base/$name temp=$base/${name}_temp in=$base/${name}_in
  publish; send_chain
  echo "== test $area: box $box"
  nas "bash -s" <<REMOTE
set -e
rm -rf '$out' '$temp' '$in'; mkdir -p '$in'
cd '$NAS_ROOT'
read e0 n0 e1 n1 <<<"\$(echo '$box' | tr ',' ' ')"
for f in data/swiss_chunks/swissalti3d_*.xyz.zip; do
  t=\${f##*/}; t=\${t#swissalti3d_*_}; e=\${t%%-*}; n=\${t#*-}; n=\${n%%_*}
  if [ \$e -ge \$e0 ] && [ \$e -lt \$e1 ] && [ \$n -ge \$n0 ] && [ \$n -lt \$n1 ]; then ln -s "\$PWD/\$f" '$in'/; fi
done
echo "source tiles: \$(ls '$in' | wc -l)"
$(chain_env "$out" "$temp" "$box" "$box" "$in")
t0=\$(date +%s)
bash '$CHAIN' 2>&1 | tee '$base/$name.out' | grep -E '^=== |FAIL|rror|Unhandled|WARN' | cut -c1-220
echo "wall: \$((\$(date +%s) - t0)) s"
echo "tiles: \$(ls '$out' | grep -c '\.terr\$') .terr, \$(ls '$out' | grep -c '\.road\$') .road, \$(ls '$out' | grep -c '\.bldg\$') .bldg, \$(ls '$out' | grep -c '\.cover\$') .cover"
echo "tile version words (offset 4) of up to 20 .terr: \$(for f in \$(ls '$out'/*.terr | head -20); do od -An -tu4 -j4 -N4 \$f; done | sort | uniq -c | tr -s ' ' | tr '\n' ';')"
grep -oE '"(formatVersion|heightOffsetM|heightScale|manifestVersion)": *[-0-9.]+' '$out/manifest.json' | sort -u | tr '\n' ' '; echo
awk '/"min":/ {v=\$2+0; if (n++==0||v<mn) mn=v} /"max":/ {v=\$2+0; if (m++==0||v>mx) mx=v} END {printf "manifest heights: min %s  max %s\n", mn, mx}' '$out/manifest.json'
REMOTE
}

case ${1:-} in
  publish) publish ;;
  start|resume) start ;;
  status) status ;;
  stop) stop ;;
  test) shift; test_run "$@" ;;
  log) nas tail -n "${2:-40}" "$LOG" ;;
  *) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
