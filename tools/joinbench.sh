#!/usr/bin/env bash
# Time to ready of a client joining a loopback server with real terrain, cold (empty --cache) then
# warm (the same cache again), N rounds (#1380, docs/notes/net/terrain-streaming.md). The client has
# no terrain of its own (--chunks on an empty folder), so everything streams or comes from the cache.
#   CHUNKS=<real terrain dir> [ROUNDS=3] [GODOT=<exe>] [PORT=] tools/joinbench.sh
# Prints one line per run (the client's "[world] ready after N s") and the medians.
. "$(dirname "$0")/lib/twoclient.sh" joinbench
PORT=${PORT:-7890}
ROUNDS=${ROUNDS:-3}
: "${CHUNKS:?set CHUNKS to the real terrain folder}"
tc_server 1800 240 "$OUT/joinbench_server.log" --server --port $PORT "${CH[@]}"
ready() { grep -ah "\[world\] ready after" "$1" | awk '{ sub(/.*ready after /, ""); sub(/ s .*/, ""); gsub(/,/, "."); print }'; }
median() { sort -n | awk '{a[NR]=$1} END{ if(NR==0) print "n/a"; else print a[int((NR+1)/2)] }'; }
join() { # <cache> <log>
  : > "$2"
  tc_client 200 "$2" --connect 127.0.0.1:$PORT --name Bench "${NOTERRAIN[@]}" --cache "$1" &
  local pid=$! i
  for i in $(seq 1 900); do
    grep -q "\[world\] ready after\|RESULT: FAILED\|connection failed" "$2" 2>/dev/null && break
    kill -0 "$pid" 2>/dev/null || break
    guard_nap 0.2
  done
  tc_stop "$pid"
}
COLD=(); WARM=()
for r in $(seq 1 "$ROUNDS"); do
  cache="$PWD/$OUT/joinbench_cache_$r"; rm -rf "$cache"; mkdir -p "$cache"
  join "$cache" "$OUT/joinbench_cold_$r.log"; c=$(ready "$OUT/joinbench_cold_$r.log")
  join "$cache" "$OUT/joinbench_warm_$r.log"; w=$(ready "$OUT/joinbench_warm_$r.log")
  echo "[joinbench] round $r: cold ${c:-?} s, warm ${w:-?} s"
  COLD+=("${c:-}"); WARM+=("${w:-}")
done
echo "[joinbench] median cold $(printf '%s\n' "${COLD[@]}" | grep . | median) s, warm $(printf '%s\n' "${WARM[@]}" | grep . | median) s"
tc_stop
