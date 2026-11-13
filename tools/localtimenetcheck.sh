#!/usr/bin/env bash
# /time where the admin stands, over loopback (#1801, src/World/LocalTimeNetProbe): a dedicated server on the
# flat fixture under the real sun, admin client A far from Switzerland (--at, where #1801 was reported) and
# client B at home. A's /time set noon must put the one shared clock at A's solar noon (the server reads A's
# place from the position A published), with A's sun at its highest; /time set 12:00 reads in A's zone. B sees
# both. No terrain data needed.
#   GODOT=<exe> [PORT=] [FAR=E,N] tools/localtimenetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" localtimenet
PORT=${PORT:-7891}
FAR=${FAR:--4679383,7259183}
WORLD="--world fixture --chunks fixture:flat --sun real --date 2026-06-21"
tc_server 300 120 "$OUT/localtimenet_server.log" --server --port $PORT $WORLD --admin-password test
tc_client 240 "$OUT/localtimenet_A.log" --connect 127.0.0.1:$PORT --name TimeA $WORLD --at "$FAR" --localtimenet A & A=$!
tc_client 240 "$OUT/localtimenet_B.log" --connect 127.0.0.1:$PORT --name TimeB $WORLD --localtimenet B & B=$!
wait $A $B
tc_stop
grep -h "\[localtimenet\|\[admin\]" "$OUT/localtimenet_A.log" "$OUT/localtimenet_B.log" "$OUT/localtimenet_server.log"
if tc_ok 2 "$OUT/localtimenet_A.log" "$OUT/localtimenet_B.log"; then echo "[localtimenetcheck] RESULT: ok"; exit 0; fi
echo "[localtimenetcheck] RESULT: FAILED (see $OUT/localtimenet_*.log)"; exit 1
