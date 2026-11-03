#!/usr/bin/env bash
# Build the terrain of all of France (RGE ALTI 1 m, #1399) on the TrueNAS box, from Git Bash on Windows.
# The runner is self-contained: the TerrainPreprocessor is published for linux-x64 with its own .NET
# runtime and copied to the NAS, so nothing is installed there and no root is needed.
#
# Usage: tools/build-france.sh <command> [args]
#   publish                  dotnet publish (self-contained linux-x64) and copy it to the NAS
#   start                    publish, then start the full run detached; running it again resumes
#   resume                   same as start
#   status                   process, progress, ETA, memory, errors
#   stop                     SIGTERM, then SIGKILL after 60 s (safe: every file is written atomically)
#   test <area> [Dxxx,...]   foreground run on a small box into terrain_chunks_france_test/<name>, prints
#                            the heights and the time. area: mont-blanc | camargue | E0,N0,E1,N1 (LV95 km,
#                            max exclusive). The départements default to the ones under the named area.
#   plan                     publish, scan the rasters and count the tiles of the full run (builds nothing)
#   log [N]                  last N lines of the run log (default 40)
# Env (all optional):
#   NAS=truenas_admin@TRUENAS        ssh target (key login, BatchMode)
#   NAS_ROOT=/mnt/Data/Documents/UnitSportSwitzerland   = X:\UnitSportSwitzerland on Windows
#   HEAP_GB=16        GC heap hard limit: the rest of the 31 GB stays for the ZFS ARC and the page cache
#   JOBS=             threads (default: all the NAS has, nproc)
#   DEM_CACHE_MB=6144 decoded French tiles kept (4 MB each); France is read ~3 times per tile otherwise
#   NICE=10           nice level
#   MIN_PROBES=1      a border tile needs data under this many of 100 probe points (1 keeps the Mont Blanc summit)
# The run: --dem lambert93,<rgealti> --dem-box auto --height-format wide --no-hash, output
# terrain_chunks_france (+ _temp for the 16 KB/tile edge cache). Nothing touches the Swiss build or X:'s
# terrain_chunks. Notes: docs/notes/tools/france.md
set -euo pipefail
cd "$(dirname "$0")/.."

NAS=${NAS:-truenas_admin@TRUENAS}
NAS_ROOT=${NAS_ROOT:-/mnt/Data/Documents/UnitSportSwitzerland}
HEAP_GB=${HEAP_GB:-16}
DEM_CACHE_MB=${DEM_CACHE_MB:-6144}
NICE=${NICE:-10}
MIN_PROBES=${MIN_PROBES:-1}
BIN_DIR=$NAS_ROOT/bin/TerrainPreprocessor-linux-x64
OUT=$NAS_ROOT/terrain_chunks_france
TEMP=${OUT}_temp
LOG=$NAS_ROOT/terrain_chunks_france.log
PIDFILE=$NAS_ROOT/terrain_chunks_france.pid
DEM=$NAS_ROOT/data/france/rgealti
PUBLISH=test_output/build-france/publish

nas() { ssh -o BatchMode=yes -o ConnectTimeout=15 "$NAS" "$@"; }

publish() {
  echo "== dotnet publish linux-x64 (self-contained)"
  dotnet publish tools/TerrainPreprocessor/TerrainPreprocessor.csproj -c Release -r linux-x64 --self-contained \
    -p:DebugType=none -o "$PUBLISH" -v q -nologo
  local size; size=$(du -sh "$PUBLISH" | cut -f1)
  echo "== copy $size to $NAS:$BIN_DIR"
  # tar over ssh: one stream, not thousands of scp round trips; the old files stay, the new ones overwrite
  nas "mkdir -p '$BIN_DIR'"
  (cd "$PUBLISH" && tar -cf - *) | nas "tar -C '$BIN_DIR' --no-overwrite-dir --no-same-owner --no-same-permissions -m -xf - && chmod +x '$BIN_DIR/TerrainPreprocessor' 2>/dev/null; test -x '$BIN_DIR/TerrainPreprocessor'"
  echo "== published"
}

alive() { nas "test -f '$PIDFILE' && kill -0 \$(cat '$PIDFILE') 2>/dev/null"; }

# the command line shared by the full run and the tests: the NAS side, run through bash (its login shell is zsh)
run_args() { # out temp [extra...]
  local out=$1 temp=$2; shift 2
  echo "--dem lambert93,$DEM --height-format wide --dem-min-probes $MIN_PROBES --dem-cache-mb $DEM_CACHE_MB --out $out --temp $temp --no-hash $*"
}

heap_hex() { printf '0x%x' $((HEAP_GB * 1024 * 1024 * 1024)); }

start() {
  if alive; then echo "already running (pid $(nas cat "$PIDFILE")); use status / stop" >&2; exit 1; fi
  publish
  local jobs=${JOBS:-$(nas nproc)}
  local args; args=$(run_args "$OUT" "$TEMP" --dem-box auto --jobs "$jobs")
  echo "== starting on the NAS: $jobs jobs, heap limit ${HEAP_GB} GB, nice $NICE, log $LOG"
  nas "bash -s" <<EOF
set -e
cd '$NAS_ROOT'
mkdir -p '$OUT' '$TEMP'
echo "=== run started \$(date '+%F %T') ===" >> '$LOG'
DOTNET_GCHeapHardLimit=$(heap_hex) DOTNET_GCConserveMemory=5 DOTNET_TieredPGO=0 \\
  setsid nohup nice -n $NICE ionice -c2 -n7 '$BIN_DIR/TerrainPreprocessor' $args >> '$LOG' 2>&1 < /dev/null &
echo \$! > '$PIDFILE'
EOF
  sleep 3
  alive && echo "== running, pid $(nas cat "$PIDFILE")" || { echo "== died at once; last log lines:" >&2; nas tail -n 20 "$LOG" >&2; exit 1; }
}

status() {
  if alive; then
    local pid; pid=$(nas cat "$PIDFILE")
    echo "running: pid $pid"
    nas "grep -E 'VmRSS|VmHWM' /proc/$pid/status | tr -s ' \t' ' '; ps -o etime=,pcpu= -p $pid | awk '{print \"elapsed \" \$1 \", cpu \" \$2 \"%\"}'"
  else
    echo "not running"
  fi
  nas "cd '$NAS_ROOT'
    echo '-- last progress:'; grep -E '^\s+\[[0-9]+/[0-9]+\]|^Terrain:|^--dem-box|tiles with its data|^Terrain done|^Horizon pass' '$LOG' 2>/dev/null | tail -n 4
    echo '-- problems: '\$(grep -cE '\[FAIL\]|\[WARN\]|Unhandled|Exception' '$LOG' 2>/dev/null || true)' lines (log tail:)'
    grep -E '\[FAIL\]|\[WARN\]|Unhandled|Exception' '$LOG' 2>/dev/null | tail -n 5
    echo '-- tiles on disk: '\$(ls '$OUT' 2>/dev/null | grep -c '\.terr\$' || true)' .terr, '\$(ls '$TEMP' 2>/dev/null | grep -c '\.edge\$' || true)' .edge'
    df -h '$NAS_ROOT' | tail -n 1"
}

stop() {
  if ! alive; then echo "not running"; return 0; fi
  local pid; pid=$(nas cat "$PIDFILE")
  echo "SIGTERM to $pid"
  nas "kill $pid; for i in \$(seq 60); do kill -0 $pid 2>/dev/null || exit 0; sleep 1; done; echo 'still alive: SIGKILL'; kill -9 $pid"
  echo "stopped; start again to resume (finished tiles are kept)"
}

box_of() {
  case $1 in
    mont-blanc) echo "2553,1073,2558,1078 D074" ;;
    camargue)   echo "2368,818,2373,823 D013" ;;
    *,*,*,*)    echo "$1 ${2:-}" ;;
    *) echo "unknown area $1" >&2; exit 2 ;;
  esac
}

test_run() {
  local area=${1:?area: mont-blanc | camargue | E0,N0,E1,N1}
  read -r box depts <<<"$(box_of "$area" "${2:-}")"
  [ -n "${depts:-}" ] || { echo "give the départements for a box, e.g. D074" >&2; exit 2; }
  local name=${area//,/_}
  local out=$NAS_ROOT/terrain_chunks_france_test/$name temp=$NAS_ROOT/terrain_chunks_france_test/${name}_temp
  publish
  local jobs=${JOBS:-$(nas nproc)}
  local args; args=$(run_args "$out" "$temp" --dem-only "$depts" --dem-box "$box" --jobs "$jobs")
  echo "== test $area: box $box, départements $depts, $jobs jobs"
  nas "bash -s" <<EOF
set -e
rm -rf '$out' '$temp'; mkdir -p '$out'
cd '$NAS_ROOT'
t0=\$(date +%s)
DOTNET_GCHeapHardLimit=$(heap_hex) DOTNET_TieredPGO=0 \
  nice -n $NICE '$BIN_DIR/TerrainPreprocessor' $args > '$out/../$name.out' 2>&1 &
pid=\$!; hwm=0
while kill -0 \$pid 2>/dev/null; do
  v=\$(awk '/VmHWM/ {print \$2}' /proc/\$pid/status 2>/dev/null || true); [ -n "\$v" ] && hwm=\$v; sleep 0.5
done
wait \$pid || echo "run FAILED"
tail -n 14 '$out/../$name.out'
echo "wall: \$((\$(date +%s) - t0)) s, peak RSS: \$((hwm / 1024)) MB"
echo "tiles: \$(ls '$out' | grep -c '\.terr\$')"
awk '/"min":/ {v=\$2+0; if (n++==0||v<mn) mn=v} /"max":/ {v=\$2+0; if (m++==0||v>mx) mx=v} END {printf "manifest heights: min %s  max %s\n", mn, mx}' '$out/manifest.json'
EOF
}

plan_run() {
  publish
  local jobs=${JOBS:-$(nas nproc)}
  local args; args=$(run_args "$TEMP.plan" "$TEMP.plan" --dem-box auto --dem-plan --jobs "$jobs")
  nas "bash -s" <<EOF
cd '$NAS_ROOT'
mkdir -p '$TEMP.plan'
DOTNET_GCHeapHardLimit=$(heap_hex) nice -n $NICE '$BIN_DIR/TerrainPreprocessor' $args
rm -rf '$TEMP.plan'
EOF
}

case ${1:-} in
  plan) plan_run ;;
  publish) publish ;;
  start|resume) start ;;
  status) status ;;
  stop) stop ;;
  test) shift; test_run "$@" ;;
  log) nas tail -n "${2:-40}" "$LOG" ;;
  *) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
