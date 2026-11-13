#!/usr/bin/env bash
# Two space shuttles in orbit over loopback (src/Player/ShuttleNetProbe, #1492): a dedicated server on the flat
# fixture with an admin password and two headless clients, both admins. Side by side on the ground, both take a
# shuttle and ask /orbit 100 together; A fires its rockets, switches the cruise drive on and off, and B sees each
# on A's copy (the pose and the drawn plumes, drive glow, re-entry's first glow at 100 km).
# Output in test_output/shuttlenet_*.log.
# No terrain data needed.
#   GODOT=<exe> [PORT=] tools/shuttlenetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" shuttlenet
PORT=${PORT:-7988}
WORLD="--world fixture --chunks fixture:flat"
tc_server 300 120 "$OUT/shuttlenet_server.log" --title "#1492 shuttlenet server" --server --port $PORT $WORLD --admin-password test
client() { tc_client 300 "$OUT/shuttlenet_$1.log" --title "#1492 shuttlenet client $1" --connect 127.0.0.1:$PORT --name "Shuttle$1" \
    $WORLD --shuttlenetcheck "$1"; }
client A & A=$!
client B & B=$!
wait $A $B
tc_stop
grep -h "\[shuttlenet\|\[shuttle\]" "$OUT/shuttlenet_A.log" "$OUT/shuttlenet_B.log"
if tc_ok 2 "$OUT/shuttlenet_A.log" "$OUT/shuttlenet_B.log"; then echo "[shuttlenetcheck] RESULT: ok"; exit 0; fi
echo "[shuttlenetcheck] RESULT: FAILED (see $OUT/shuttlenet_*.log)"; exit 1
