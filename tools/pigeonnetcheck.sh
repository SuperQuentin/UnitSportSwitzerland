#!/usr/bin/env bash
# Playing the pigeon over loopback (src/Birds/PigeonNetProbe, #217): a dedicated server (--generated-world)
# and two headless clients. A turns into a pigeon (no items, items kept), B sees a bird, A flies over B and
# lets go (B splatted and told by whom, A scores), A turns back (items back). Output in test_output/pigeonnet_*.log.
#   GODOT=<exe> [PORT=] [SERVER_ARGS=] tools/pigeonnetcheck.sh [E,N]
. "$(dirname "$0")/lib/twoclient.sh" pigeonnet
AT=${1:-2583250,1113250}
PORT=${PORT:-7797}
tc_server 300 120 "$OUT/pigeonnet_server.log" --title "#217 pigeonnet server" --server ${SERVER_ARGS---generated-world} --port $PORT
client() { tc_client 300 "$OUT/pigeonnet_$1.log" --title "#217 pigeonnet client $1" --connect 127.0.0.1:$PORT --name "Pigeon$1" \
    --cache "$OUT/pigeonnet_cache_$1" --at "$AT" --pigeonnetcheck "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[pigeonnet\|by peer" "$OUT/pigeonnet_A.log" "$OUT/pigeonnet_B.log"
if tc_ok 2 "$OUT/pigeonnet_A.log" "$OUT/pigeonnet_B.log"; then echo "[pigeonnetcheck] RESULT: ok"; exit 0; fi
echo "[pigeonnetcheck] RESULT: FAILED (see $OUT/pigeonnet_*.log)"; exit 1
