#!/usr/bin/env bash
# Load test over loopback: one headless dedicated server (--serverstats), <players>-1 bots in
# --swarm processes of up to 16 bots each (src/Net/Swarm.cs), and one real observer client that
# measures how smooth a remote player looks (--netsmooth, src/Net/NetSmoothProbe.cs), parked
# beside the Mollendruz race pack. Everything lands in test_output/loadtest/<label>/.
#   tools/loadtest.sh <players> [label] [seconds]     (label: p<players>, seconds: 90)
# Env: GODOT (default godot), CHUNKS (terrain_chunks dir, default the project's), PORT (7797),
#      SEED (1), AT (observer E,N, default Col du Mollendruz).
set -u
PLAYERS=${1:?usage: tools/loadtest.sh <players> [label] [seconds]}
LABEL=${2:-p$PLAYERS}
SECONDS_RUN=${3:-90}
GODOT=${GODOT:-godot}
PORT=${PORT:-7797}
SEED=${SEED:-1}
AT=${AT:-2518038,1167321}
cd "$(dirname "$0")/.."
ROOT=$(pwd)
CHUNKS=${CHUNKS:-$ROOT/terrain_chunks}
[ -f "$CHUNKS/manifest.json" ] || { echo "no terrain in $CHUNKS (set CHUNKS=<dir>)"; exit 1; }
OUT=$ROOT/test_output/loadtest/$LABEL
rm -rf "$OUT"; mkdir -p "$OUT"
BOTS=$((PLAYERS - 1))
(( BOTS >= 1 && PLAYERS <= 32 )) || { echo "players must be 2..32 (NetworkManager.MaxClients)"; exit 1; }

# the default user cache must not keep a server manifest from this run (see
# docs/notes/net/loopback-server-test-leaves-manifest.md); every process gets its own --cache
USERDATA="$HOME/.local/share/godot/app_userdata"; [ -n "${APPDATA:-}" ] && USERDATA="$(cygpath -u "$APPDATA")/Godot/app_userdata"   # Linux / Windows (Git Bash)
USER_MANIFEST="$USERDATA/UnitSportSwitzerland/chunk_cache/server-manifest.json"
HAD_MANIFEST=0; [ -f "$USER_MANIFEST" ] && HAD_MANIFEST=1

# the machine may be shared: say how much memory there was, and warn when it is short (a full
# client takes ~2 GB and the OOM killer invalidates a run by killing one of its processes)
# (Git Bash has no MemAvailable: MemFree there)
avail_mb() { awk '/MemAvailable/ { a = $2 } /MemFree/ { f = $2 } END { printf "%d", (a ? a : f) / 1024 }' /proc/meminfo; }
MEM_START=$(avail_mb); MEM_MIN=$MEM_START
(( MEM_START < 5000 )) && echo "[loadtest] WARNING: only $MEM_START MB available"
echo "[loadtest] $LABEL: $BOTS bots + 1 observer, ${SECONDS_RUN}s, chunks $CHUNKS, $MEM_START MB available"
"$GODOT" --headless --path . -- --title "loadtest $LABEL server" --server --port $PORT --serverstats,$LABEL --chunks "$CHUNKS" \
    --seconds $((SECONDS_RUN + 40)) > "$OUT/server.log" 2>&1 &
SERVER=$!
for _ in $(seq 60); do grep -q "server listening" "$OUT/server.log" 2>/dev/null && break; sleep 0.5; done

# bots split evenly over ceil(BOTS/16) processes; each hosts its slice of one shared plan
PROCS=$(( (BOTS + 15) / 16 ))
SWARMS=(); COUNTS=()
FIRST=0
for ((k = 0; k < PROCS; k++)); do
    N=$(( BOTS / PROCS + (k < BOTS % PROCS ? 1 : 0) ))
    "$GODOT" --headless --path . -- --title "loadtest $LABEL swarm$k" --swarm $N --first $FIRST --total $BOTS --seed $SEED \
        --connect 127.0.0.1:$PORT --chunks "$CHUNKS" --cache "$OUT/swarm${k}_cache" \
        --seconds $((SECONDS_RUN + 25)) > "$OUT/swarm$k.log" 2>&1 &
    SWARMS+=($!); COUNTS+=($N)
    FIRST=$((FIRST + N))
done
sleep $(( 4 + BOTS / 8 ))

# the observer measures SECONDS_RUN-20 s once it has picked a target (after ~6 s)
OBS_SECONDS=$(( SECONDS_RUN > 40 ? SECONDS_RUN - 20 : 20 ))
"$GODOT" --headless --path . -- --title "loadtest $LABEL observer" --connect 127.0.0.1:$PORT --netsmooth,$OBS_SECONDS,$LABEL --at "$AT" \
    --traffic 0 --chunks "$CHUNKS" --cache "$OUT/observer_cache" > "$OUT/observer.log" 2>&1 &
OBSERVER=$!

# CPU time of the swarm processes over the steady part of the run. Git Bash's ps has no -o, and
# its pid is godot's console wrapper: read the Godot process it started instead (integers only,
# no locale decimals)
if [ -r "/proc/$SERVER/winpid" ]; then
    declare -A WPID
    for p in "${SWARMS[@]}"; do
        w=$(cat /proc/$p/winpid)
        c=$(powershell.exe -NoProfile -Command "(Get-CimInstance Win32_Process -Filter \"ParentProcessId=$w AND Name LIKE 'Godot%'\" | Select-Object -First 1).ProcessId" 2>/dev/null | tr -d '\r')
        WPID[$p]=${c:-$w}
    done
    wps() { powershell.exe -NoProfile -Command "\$p = Get-Process -Id ${WPID[$1]:-0} -ErrorAction SilentlyContinue; if (\$p) { \$p.TotalProcessorTime.Ticks; \$p.WorkingSet64 }" 2>/dev/null | tr -d '\r'; }
    cpu_s() { wps $1 | sed -n 1p | awk '{ printf "%.2f", $1 / 1e7 }'; }
    rss_kb() { wps $1 | sed -n 2p | awk '{ printf "%d", $1 / 1024 }'; }
else
    cpu_s() { ps -o times= -p "$1" 2>/dev/null | tr -d ' '; }
    rss_kb() { ps -o rss= -p "$1" 2>/dev/null | tr -d ' '; }
fi
sleep 10
declare -A CPU0; T0=$(date +%s.%N)
for p in "${SWARMS[@]}"; do CPU0[$p]=$(cpu_s $p); done
declare -A RSS_PEAK
END=$(( $(date +%s) + OBS_SECONDS ))
while (( $(date +%s) < END )); do
    for p in "${SWARMS[@]}"; do
        r=$(rss_kb $p); r=${r:-0}
        (( r > ${RSS_PEAK[$p]:-0} )) && RSS_PEAK[$p]=$r
    done
    m=$(avail_mb); (( m < MEM_MIN )) && MEM_MIN=$m
    sleep 5
done
T1=$(date +%s.%N)
SWARM_LINES=()
for k in "${!SWARMS[@]}"; do
    p=${SWARMS[$k]}
    SWARM_LINES+=("$(awk -v a="${CPU0[$p]:-0}" -v b="$(cpu_s $p)" -v t0=$T0 -v t1=$T1 -v rss="${RSS_PEAK[$p]:-0}" -v n=${COUNTS[$k]} \
        'BEGIN { c = 100*(b-a)/(t1-t0); printf "%d bots: peak rss %.0f MB (%.0f MB/bot), cpu %.0f %% of a core (%.1f %%/bot)", n, rss/1024, rss/1024/n, c, c/n }')")
done

# everyone quits on their own (--seconds, --netsmooth); anything still up after that is killed.
# Exit codes: 139 after the results is the known headless segfault on exit
# (docs/notes/general/headless-exit-139.md); 137 is SIGKILL, usually the OOM killer.
wait_or_kill() { for _ in $(seq $2); do kill -0 $1 2>/dev/null || break; sleep 1; done
    kill -0 $1 2>/dev/null && { kill $1; sleep 2; kill -9 $1 2>/dev/null; }; wait $1 2>/dev/null; }
EXITS=""; INVALID=0
wait_or_kill $OBSERVER 60; c=$?; EXITS+="observer=$c "; (( c == 137 )) && INVALID=1
for k in "${!SWARMS[@]}"; do wait_or_kill ${SWARMS[$k]} 60; c=$?; EXITS+="swarm$k=$c "; (( c == 137 )) && INVALID=1; done
wait_or_kill $SERVER 60; c=$?; EXITS+="server=$c"; (( c == 137 )) && INVALID=1

if (( HAD_MANIFEST == 0 )) && [ -f "$USER_MANIFEST" ]; then rm -f "$USER_MANIFEST"; echo "[loadtest] removed a server-manifest.json from the default cache"; fi

{
    echo "==== loadtest $LABEL: $PLAYERS players ($BOTS bots in $PROCS swarm process(es) + observer), ${SECONDS_RUN}s"
    echo "---- server"
    grep -v "^last_window" "$OUT/server_summary.txt" 2>/dev/null || echo "(no server summary)"
    echo "---- observer (remote smoothness)"
    grep -v "^label" "$OUT/netsmooth.txt" 2>/dev/null || echo "(no netsmooth result)"
    grep -h "\[netsmooth\] watching" "$OUT/observer.log"
    echo "---- swarm processes"
    for k in "${!SWARMS[@]}"; do
        echo "swarm$k ${SWARM_LINES[$k]}; $(grep -h "^\[swarm\] t=" "$OUT/swarm$k.log" | tail -2 | head -1)"
    done
    echo "dropped connections: $(cat "$OUT"/swarm*.log | grep -c 'server disconnected') bots$(grep -q 'kicked\|ServerDisconnected\|target left' "$OUT/observer.log" && echo ', observer lost its target')"
    echo "memory available: $MEM_START MB at start, $MEM_MIN MB lowest; exit codes: $EXITS"
    (( INVALID )) && echo "RUN INVALID: a process was SIGKILLed (OOM killer?); rerun on a quieter machine"
    echo "---- errors per log (ERROR lines)"
    for f in "$OUT"/*.log; do echo "$(basename "$f"): $(grep -c '^ERROR' "$f")"; done
} | tee "$OUT/summary.txt"
