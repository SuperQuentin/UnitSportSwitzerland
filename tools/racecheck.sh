#!/usr/bin/env bash
# Multiplayer race over loopback: a dedicated server and --raceauto clients (AutoPilot on the
# ground, GatePilot in the air). Client A opens a race, client B joins it; passes when the server
# classifies both.
#   tools/racecheck.sh [car|bike|foot|skis|moto|plane|heli|paraglider|wingsuit] [E,N] [metres|place]
#   tools/racecheck.sh skip   a plane race where B never reports gate 1: A classified, B refused
#   tools/racecheck.sh two    two races at once: a car duel A-B at the Col du Mollendruz and a
#                             plane race C-D near L'Isle; B also tries to join the plane race
#                             and must be refused (already racing)
# Defaults: ground at the Col du Mollendruz over 1500 m; air from Mont-la-Ville to Montricher.
# Each client gets its own --cache (a loopback run leaves the server manifest in the cache).
# CHUNKS=<dir> passes --chunks <dir> to every process (a worktree has no terrain_chunks of its own).
set -u
CHUNKARGS=(); [ -n "${CHUNKS:-}" ] && CHUNKARGS=(--chunks "$CHUNKS")
CLASS=${1:-car}
OUT=test_output
PORT=$((7790 + RANDOM % 100))
cd "$(dirname "$0")/.."
mkdir -p "$OUT"
PIDS=()
cleanup() { for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null; done; rm -rf "${CACHES[@]}" 2>/dev/null; }
CACHES=()
trap cleanup EXIT

client() {   # client <log> <args...>
    local log=$1; shift
    local cache; cache=$(mktemp -d); CACHES+=("$cache")
    timeout 400 godot --headless --path . -- "${CHUNKARGS[@]}" --connect 127.0.0.1:$PORT --cache "$cache" --traffic 0 --raceauto "$@" \
        > "$OUT/$log" 2>&1 &
    PIDS+=($!)
}

wait_results() {   # wait_results <count> <seconds>
    for _ in $(seq 1 "$2"); do
        [ "$(grep -c "\[race\] #[0-9]* .* results:" $OUT/racecheck_server.log)" -ge "$1" ] && return 0
        # nobody drives: this class has no autopilot yet, nothing to wait for
        [ "$(grep -l "no autopilot" $OUT/racecheck_a.log $OUT/racecheck_b.log 2>/dev/null | wc -l)" -ge 2 ] && return 1
        sleep 1
    done
    return 1
}

AIR_AT=2521250,1166750
case "$CLASS" in
    car|bike|foot|skis|moto) AT=${2:-2518038,1167321}; WHAT="${3:-1500} $CLASS" ;;
    plane|heli|paraglider|wingsuit|skip)
        AT=${2:-$AIR_AT}
        M=plane; [ "$CLASS" != skip ] && M=$CLASS
        WHAT="air ${3:-Montricher} $M" ;;
    two) ;;
    *) echo "unknown class $CLASS"; exit 2 ;;
esac

timeout 420 godot --headless --path . -- "${CHUNKARGS[@]}" --server --port $PORT > $OUT/racecheck_server.log 2>&1 &
PIDS+=($!)
sleep 6

if [ "$CLASS" = two ]; then
    client racecheck_a.log --name Takumi --at 2518038,1167321 --racecmd "duel Keisuke 1500 car"
    sleep 1
    client racecheck_b.log --name Keisuke --at 2518038,1167321 --racejoin
    sleep 12
    client racecheck_c.log --name Ryosuke --at $AIR_AT --racestart "air Montricher plane"
    sleep 1
    client racecheck_d.log --name Kyoichi --at $AIR_AT --racejoin Ryosuke
    wait_results 2 360
    grep -h "results:" $OUT/racecheck_server.log
    grep -h "already in race" $OUT/racecheck_b.log | head -1
    if [ "$(grep -c "results: 1\..*2\." $OUT/racecheck_server.log)" -ge 2 ] && grep -q "already in race" $OUT/racecheck_b.log; then
        echo "[racecheck] RESULT: ok (two races at once, second entry refused)"; exit 0
    fi
    echo "[racecheck] RESULT: FAILED (see $OUT/racecheck_*.log)"; exit 1
fi

client racecheck_a.log --name Takumi --at "$AT" --racestart "$WHAT"
sleep 2
if [ "$CLASS" = skip ]; then client racecheck_b.log --name Keisuke --at "$AT" --racejoin --raceskip 1
else client racecheck_b.log --name Keisuke --at "$AT" --racejoin; fi
wait_results 1 360
grep -h "results:" $OUT/racecheck_server.log
grep -h "no autopilot" $OUT/racecheck_a.log | head -1
if [ "$CLASS" = skip ]; then
    if grep -q "results: 1\. Takumi.*DNF: Keisuke" $OUT/racecheck_server.log && grep -q "Keisuke crossed the line with .* missed" $OUT/racecheck_server.log; then
        echo "[racecheck] RESULT: ok (skipped gate refused)"; exit 0
    fi
elif grep -q "results: 1\..*2\." $OUT/racecheck_server.log; then
    echo "[racecheck] RESULT: ok"; exit 0
fi
echo "[racecheck] RESULT: FAILED (see $OUT/racecheck_*.log)"; exit 1
