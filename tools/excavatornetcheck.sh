#!/usr/bin/env bash
# An excavator another player digs with, over loopback (#611, src/Player/ExcavatorNetProbe): a
# dedicated server on the flat fixture with an admin password and two headless clients. A takes an
# excavator, drives it, works its arm in dig mode on the real bindings; B draws A's arm at A's
# angles; A gets out and B's parked machine keeps the arm A left. No terrain data needed. Output in
# test_output/excavatornet_*.log.
#   GODOT=<exe> [PORT=] tools/excavatornetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" excavatornet
PORT=${PORT:-7867}
WORLD="--world fixture --chunks fixture:flat --traffic 0"
tc_server 300 120 "$OUT/excavatornet_server.log" --server --port $PORT --world fixture --chunks fixture:flat --admin-password test
client() { tc_client 240 "$OUT/excavatornet_$1.log" --connect 127.0.0.1:$PORT --name "Digger$1" $WORLD --excavatornet "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[excavatornet" "$OUT/excavatornet_A.log" "$OUT/excavatornet_B.log"
if tc_ok 2 "$OUT/excavatornet_A.log" "$OUT/excavatornet_B.log"; then echo "[excavatornetcheck] RESULT: ok"; exit 0; fi
echo "[excavatornetcheck] RESULT: FAILED (see $OUT/excavatornet_*.log)"; exit 1
