#!/usr/bin/env bash
# The live map's file (#896, docs/notes/net/live-map.md): a dedicated server on the fixture with
# --livemap-file, and a swarm process with two bot players joining it. players.json must come to list
# both, online, with LV95 positions inside Switzerland and a fresh time; once the swarm has left it
# must list nobody (written on change, the heartbeat aside).
#   GODOT=<exe> [PORT=] tools/livemapcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" livemap
PORT=${PORT:-7883}
WORLD="--world fixture --chunks fixture:flat"
FILE="$OUT/livemap_players.json"
rm -f "$FILE"
tc_server 300 120 "$OUT/livemap_server.log" --server --port $PORT $WORLD --livemap-file "$FILE"
tc_client 120 "$OUT/livemap_swarm.log" --swarm 2 --connect 127.0.0.1:$PORT $WORLD --seconds 35 &
SWARM=$!

# python reads the file: two players, online, inside Switzerland, written within the last 10 s
ok_with() { python -I - "$FILE" "$1" <<'EOF'
import json, sys, time
try:
    d = json.load(open(sys.argv[1], encoding="utf-8"))
except Exception:
    sys.exit(1)
want = int(sys.argv[2])
inside = all(2480000 < p["e"] < 2840000 and 1070000 < p["n"] < 1300000 and p["name"] for p in d["players"])
fresh = time.time() - d["updated"] < 10
sys.exit(0 if d["online"] and len(d["players"]) == want and inside and fresh else 1)
EOF
}

code=1
for i in $(seq 1 90); do
  if ok_with 2; then echo "[livemap] ok   both swarm players on the map: $(cat "$FILE")"; code=0; break; fi
  sleep 1
done
[ $code = 0 ] || echo "[livemap] FAIL the swarm never showed on the map: $(cat "$FILE" 2>/dev/null)"
wait $SWARM
if [ $code = 0 ]; then
  code=1
  for i in $(seq 1 30); do ok_with 0 && { echo "[livemap] ok   nobody once the swarm left"; code=0; break; }; sleep 1; done
  [ $code = 0 ] || echo "[livemap] FAIL players still listed after the swarm left: $(cat "$FILE")"
fi
tc_stop
grep -h "\[livemap\]" "$OUT/livemap_server.log"
echo "[livemapcheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
