#!/usr/bin/env bash
# The admin item, money and vehicle commands over loopback (#32, #262, src/Items/EconomyProbe): a
# dedicated server on the flat fixture with an admin password and one client that is refused as a
# plain player, logs in, then spawns, gives, pays and finally /despawns its bike and a dropped
# stone (#1200), which must leave its own world through the server. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/econcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" econ
PORT=${PORT:-7877}
WORLD="--world fixture --chunks fixture:flat"
tc_server 300 120 "$OUT/econ_server.log" --server --port $PORT $WORLD --admin-password econcheck
tc_client 240 "$OUT/econ_client.log" --connect 127.0.0.1:$PORT --name Econ $WORLD --traffic 0 --econcheck econcheck
tc_stop
grep -h "\[econ\]" "$OUT/econ_client.log"
grep -h "\[despawn\]\|\[vehicles\]" "$OUT/econ_server.log"
if tc_ok 1 "$OUT/econ_client.log"; then echo "[econcheck] RESULT: ok"; exit 0; fi
echo "[econcheck] RESULT: FAILED (see $OUT/econ_*.log)"; exit 1
