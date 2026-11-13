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
#   cube <command>          the earth leaves (cube-sphere L14 .cubl + cube_manifest.json, #1794) of one region
#                           into terrain_chunks_cube, every stage of MapSetup's cube layer, no pyramid
#                           (tools/lib/cube-nas-chain.sh; docs/notes/tools/cube-nas-run.md):
#     cube check            the sources every stage reads, on the NAS: what is missing and how to fetch it
#     cube start | resume   publish, check, run the stages detached; FROM=<stage> redoes that stage and the later ones
#                           (heights roads buildings sources places network cover water covernet trees fields pistes files)
#     cube status | stop | log [N]
#     cube test [geneva|E0,N0,E1,N1]   every stage, foreground, on a small LV95 box in metres (geneva:
#                           2499800,1117300,2501000,1118500, 9 leaves) into terrain_chunks_cube_test/<name>
#     the region: TILES=<file> (LV95 km tiles, E-N a line; default tools/regions/d2-server.tiles, the game
#     server's: B4's 825 tiles, Geneva to Riddes) or BOX=E0,N0,E1,N1 (LV95 metres); CUBE_OUT=<dir on the NAS>
#     (default NAS_ROOT/terrain_chunks_cube); FRAMES= network frames side by side (default by the heap, ~5 GB
#     each); ALTI2M=1 / RGEALTI=1 add the 2 m swissALTI3D / IGN RGE ALTI heights; NO_OSM=1 builds without OSM;
#     HEAP_GB defaults to 20 for cube
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
HEAP_GIVEN=${HEAP_GB:-}
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
# outside the repository: a build under the checkout's folders disturbs the game's own build
PUBLISH=${PUBLISH:-${TMPDIR:-/tmp}/unitsport-nas/build-swiss-nas-publish}

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
rc=\${PIPESTATUS[0]}
echo "wall: \$((\$(date +%s) - t0)) s"
trap '[ \$rc = 0 ] || { echo "TEST FAILED: the chain exited \$rc"; exit \$rc; }' EXIT
echo "tiles: \$(ls '$out' | grep -c '\.terr\$') .terr, \$(ls '$out' | grep -c '\.road\$') .road, \$(ls '$out' | grep -c '\.bldg\$') .bldg, \$(ls '$out' | grep -c '\.cover\$') .cover"
echo "tile version words (offset 4) of up to 20 .terr: \$(for f in \$(ls '$out'/*.terr | head -20); do od -An -tu4 -j4 -N4 \$f; done | sort | uniq -c | tr -s ' ' | tr '\n' ';')"
grep -oE '"(formatVersion|heightOffsetM|heightScale|manifestVersion)": *[-0-9.]+' '$out/manifest.json' | sort -u | tr '\n' ' '; echo
awk '/"min":/ {v=\$2+0; if (n++==0||v<mn) mn=v} /"max":/ {v=\$2+0; if (m++==0||v>mx) mx=v} END {printf "manifest heights: min %s  max %s\n", mn, mx}' '$out/manifest.json'
REMOTE
}

# ---- the earth leaves (#1794) ------------------------------------------------------------------------------------
CUBE_CHAIN=$NAS_ROOT/bin/cube-nas-chain.sh
CUBE_OUT=${CUBE_OUT:-$NAS_ROOT/terrain_chunks_cube}

# cube_region: the selection as a tile list in $CUBE_TILES (local) and its LV95 box in metres in $CUBE_BOX
cube_region() {
  CUBE_TILES=$(mktemp)
  if [ -n "${BOX:-}" ] && [ -z "${TILES:-}" ]; then
    # the box picks the leaves (by their centre); their sources are every km tile within 1 km of it, since a leaf
    # (~600 m) reaches up to ~430 m past the box: without them its outer samples would have no heights
    awk -v b="$BOX" 'BEGIN { split(b, v, ","); for (e = int(v[1] / 1000) - 1; e * 1000 < v[3] + 1000; e++) for (n = int(v[2] / 1000) - 1; n * 1000 < v[4] + 1000; n++) print e "-" n }' > "$CUBE_TILES"
    CUBE_BOX=$BOX
    # the margin's tiles may lie abroad or in a lake: a box's missing tiles are reported, not fatal
    MISSING_TILES_OK=${MISSING_TILES_OK-1}
  else
    local f=${TILES:-tools/regions/d2-server.tiles}
    [ -f "$f" ] || { echo "no tile list $f" >&2; exit 2; }
    sed 's/#.*//; s/[,_]/-/; s/[[:space:]]//g; /^$/d' "$f" > "$CUBE_TILES"
    # the tiles' bounds, as MapSetup's Selection.Bounds() (what B4's --cube-box was)
    CUBE_BOX=$(awk -F- 'NR == 1 { e0 = e1 = $1; n0 = n1 = $2 }
      { if ($1 < e0) e0 = $1; if ($1 > e1) e1 = $1; if ($2 < n0) n0 = $2; if ($2 > n1) n1 = $2 }
      END { printf "%d,%d,%d,%d", e0 * 1000, n0 * 1000, (e1 + 1) * 1000, (n1 + 1) * 1000 }' "$CUBE_TILES")
  fi
  echo "== region: $(wc -l < "$CUBE_TILES" | tr -d ' ') LV95 km tiles, box $CUBE_BOX (LV95 m)"
}

# cube_send <out>: the chain script and the selection's tile list onto the NAS
cube_send() {
  nas "mkdir -p '$NAS_ROOT/bin' '${1}_temp' && cat > '$CUBE_CHAIN'" < tools/lib/cube-nas-chain.sh
  nas "cat > '${1}_temp/region.tiles'" < "$CUBE_TILES"
}

# the environment the cube chain reads, as `export` lines
cube_env() { # out
  local frames=${FRAMES:-$(( (HEAP_GB - 6) / 5 ))}
  [ "$frames" -lt 1 ] && frames=1
  [ "$frames" -gt 4 ] && frames=4
  cat <<ENV
export OUT='$1' TEMP='${1}_temp' DATA='$NAS_ROOT/data' BIN='$BIN_DIR' NICE=$NICE BOX='$CUBE_BOX' FRAMES=$frames
export TILES_FILE='${1}_temp/region.tiles' IN_DIR='${1}_temp/alti_in' FROM='${FROM:-}' MISSING_TILES_OK='${MISSING_TILES_OK:-}'
export ALTI2M='${ALTI2M:-}' RGEALTI='${RGEALTI:-}' NO_OSM='${NO_OSM:-}' CHECK_ONLY='${CHECK_ONLY:-}'
export JOBS=${JOBS:-\$(nproc)}
export DOTNET_GCHeapHardLimit=$(heap_hex)
ENV
}

# the sources, in the foreground: exit 3 with what to fetch when one is missing
cube_check() { # out
  nas "bash -s" <<REMOTE
$(CHECK_ONLY=1 cube_env "$1")
bash '$CUBE_CHAIN'
REMOTE
}

cube() {
  HEAP_GB=${HEAP_GIVEN:-20}   # B4 peaked at 10.8 GB (one network frame), the 2 m corridor's water at 13.0 GB
  LOG=$CUBE_OUT.log PIDFILE=$CUBE_OUT.pid
  local cmd=${1:-}
  [ $# -gt 0 ] && shift
  case $cmd in
    check)
      cube_region; cube_send "$CUBE_OUT"; cube_check "$CUBE_OUT"
      ;;
    start|resume)
      if alive; then echo "already running (pid $(nas cat "$PIDFILE")); use cube status / cube stop" >&2; exit 1; fi
      cube_region; cube_send "$CUBE_OUT"
      cube_check "$CUBE_OUT"   # a missing source stops here, before anything is published or detached
      publish
      echo "== starting on the NAS: $CUBE_OUT, heap limit ${HEAP_GB} GB, nice $NICE, from '${FROM:-first unfinished stage}', log $LOG"
      nas "bash -s" <<REMOTE
set -e
cd '$NAS_ROOT'
$(cube_env "$CUBE_OUT")
echo "=== run started \$(date '+%F %T') ===" >> '$LOG'
setsid nohup bash '$CUBE_CHAIN' >> '$LOG' 2>&1 < /dev/null &
echo \$! > '$PIDFILE'
REMOTE
      sleep 3
      alive && echo "== running, pid $(nas cat "$PIDFILE")" || { echo "== died at once; last log lines:" >&2; nas tail -n 20 "$LOG" >&2; exit 1; }
      ;;
    status)
      if alive; then
        local pid; pid=$(nas cat "$PIDFILE")
        echo "running: chain pid $pid"
        nas "for p in \$(pgrep -P $pid; echo $pid); do grep -E 'Name|VmRSS|VmHWM' /proc/\$p/status 2>/dev/null | tr -s ' \t' ' ' | tr '\n' ' '; echo; done; ps -o etime= -p $pid | awk '{print \"elapsed \" \$1}'"
      else
        echo "not running"
      fi
      nas "bash -s" <<REMOTE
echo '-- stages:'; grep -E '^=== .* (stage|ALL|run started|stopped|SOURCES|leaves:)' '$LOG' 2>/dev/null | tail -n 10
echo "-- done markers: \$(ls '${CUBE_OUT}_temp' 2>/dev/null | grep '^stage_' | sed 's/^stage_//; s/[.]done\$//' | tr '\n' ' ')"
echo "-- problems: \$(grep -cE 'FAILED|Unhandled|Exception|MISSING' '$LOG' 2>/dev/null || true) lines (last:)"
grep -E 'FAILED|Unhandled|Exception|MISSING' '$LOG' 2>/dev/null | tail -n 5 | cut -c1-200
echo "-- leaves on disk: \$(ls '$CUBE_OUT' 2>/dev/null | grep -c '[.]cubl\$' || true) bundles, \$(du -sh '$CUBE_OUT' 2>/dev/null | cut -f1)"
df -h '$NAS_ROOT' | tail -n 1
REMOTE
      ;;
    stop)
      if ! alive; then echo "not running"; return 0; fi
      local pid; pid=$(nas cat "$PIDFILE")
      echo "SIGTERM to $pid"
      nas "kill $pid; for i in \$(seq 60); do kill -0 $pid 2>/dev/null || exit 0; sleep 1; done; echo 'still alive: SIGKILL'; kill -9 -- -$pid"
      echo "stopped; cube start again to resume (finished stages are kept, the stopped one starts over)"
      ;;
    log) nas tail -n "${1:-40}" "$LOG" ;;
    test)
      local area=${1:-geneva}
      case $area in
        geneva) BOX=2499800,1117300,2501000,1118500 ;;   # D1's Geneva box (#1711): 9 leaves, 4 with roads
        *,*,*,*) BOX=$area ;;
        *) echo "cube test: geneva | E0,N0,E1,N1 (LV95 metres)" >&2; exit 2 ;;
      esac
      TILES=
      local out=$NAS_ROOT/terrain_chunks_cube_test/${area//,/_}
      nas "rm -rf '$out' '${out}_temp'"
      cube_region; cube_send "$out"
      cube_check "$out"
      publish
      echo "== cube test $area: box $BOX -> $out"
      nas "bash -s" <<REMOTE
set -e
$(cube_env "$out")
t0=\$(date +%s)
bash '$CUBE_CHAIN' 2>&1 | tee '$out.out' | grep -E '^=== |FAIL|rror|Unhandled|WARN' | cut -c1-220
rc=\${PIPESTATUS[0]}
trap '[ \$rc = 0 ] || { echo "TEST FAILED: the chain exited \$rc"; exit \$rc; }' EXIT
echo "wall: \$((\$(date +%s) - t0)) s (full output: $out.out)"
echo "kinds: \$(tr -d ' \n' < '$out/cube_manifest.json' | grep -o '\"kinds\":\[[^]]*\]' | head -1)"
echo "region files: \$(ls '$out' | grep -v '[.]cubl\$' | tr '\n' ' ')"
REMOTE
      ;;
    *) sed -n '/^#   cube <command>/,/^# Env/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 2 ;;
  esac
}

case ${1:-} in
  cube) shift; cube "$@" ;;
  publish) publish ;;
  start|resume) start ;;
  status) status ;;
  stop) stop ;;
  test) shift; test_run "$@" ;;
  log) nas tail -n "${2:-40}" "$LOG" ;;
  *) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
