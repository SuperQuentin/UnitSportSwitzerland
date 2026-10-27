#!/usr/bin/env bash
# /locate over loopback (#1277, src/World/Locate/LocateService + LocateNetProbe): a dedicated server on the garage
# course with the fixture POIs builds its building index at boot; a client asks /locate, gets the blocks' garage
# doors and the fixture castle from the server (building no index of its own), and /go on an answer moves it there.
#   GODOT=<exe> [PORT=] tools/locatenetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" locatenet
PORT=${PORT:-7931}
WORLD="--world fixture --chunks fixture:garage --pois fixture"
tc_server 240 120 "$OUT/locatenet_server.log" --server --port $PORT $WORLD --systems network
tc_client 200 "$OUT/locatenet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD --systems network,physics,ui --locatenet a
tc_stop
grep -h "\[locate" "$OUT/locatenet_server.log" "$OUT/locatenet_A.log"
if tc_ok 1 "$OUT/locatenet_A.log" && grep -q "\[locate\] building index" "$OUT/locatenet_server.log"; then
  echo "[locatenetcheck] RESULT: ok"; exit 0
fi
echo "[locatenetcheck] RESULT: FAILED (see $OUT/locatenet_*.log)"; exit 1
