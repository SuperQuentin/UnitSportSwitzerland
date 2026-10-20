#!/usr/bin/env bash
# The world picker online (#999, src/Ui/WorldPickCheck, docs/notes/core/test-worlds.md): a dedicated
# server started with --test-world junction, and one client from the title that (1) picks the
# junction course on Play solo, (2) hosts the junction course from the menu (its status names it),
# (3) joins the dedicated server as from a typed address, in the open world: the server's rules say
# junction and the shell joins again on the course. No terrain data needed.
#   GODOT=<exe> [PORT=] [HOSTPORT=] tools/worldpicknetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" worldpicknet
PORT=${PORT:-7883}
HOSTPORT=${HOSTPORT:-7885}
tc_server 400 120 "$OUT/worldpicknet_server.log" --server --port $PORT --test-world junction --traffic 0
tc_client 360 "$OUT/worldpicknet_A.log" --worldpickcheck host $HOSTPORT connect 127.0.0.1:$PORT nosignals --world fixture --name Picker
tc_stop
grep -h "\[worldpick\]\|test world\|again in world\|plays world" "$OUT/worldpicknet_server.log" "$OUT/worldpicknet_A.log"
if tc_ok 1 "$OUT/worldpicknet_A.log"; then echo "[worldpicknetcheck] RESULT: ok"; exit 0; fi
echo "[worldpicknetcheck] RESULT: FAILED (see $OUT/worldpicknet_*.log)"; exit 1
