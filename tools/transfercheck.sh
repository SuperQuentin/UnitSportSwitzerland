#!/usr/bin/env bash
# /transfer over loopback (#649, src/Net/ChatManager + TransferProbe): a dedicated server on the flat
# fixture, clients A and B with the starter kit. A gives B its whole inventory with
# /transfer me TransferB: A must end empty, B must have more. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/transfercheck.sh
. "$(dirname "$0")/lib/twoclient.sh" transfer
PORT=${PORT:-7881}
WORLD="--world fixture --chunks fixture:flat"
tc_server 300 120 "$OUT/transfer_server.log" --server --port $PORT $WORLD
tc_client 240 "$OUT/transfer_A.log" --connect 127.0.0.1:$PORT --name TransferA $WORLD --transfer A & A=$!
sleep 8
tc_client 220 "$OUT/transfer_B.log" --connect 127.0.0.1:$PORT --name TransferB $WORLD --transfer B & B=$!
wait $A $B
tc_stop
/usr/bin/grep -h "\[transfer" "$OUT/transfer_A.log" "$OUT/transfer_B.log" "$OUT/transfer_server.log"
if tc_ok 2 "$OUT/transfer_A.log" "$OUT/transfer_B.log"; then echo "[transfercheck] RESULT: ok"; exit 0; fi
echo "[transfercheck] RESULT: FAILED (see $OUT/transfer_*.log)"; exit 1
