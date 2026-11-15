#!/usr/bin/env bash
# A bot on a dedicated server flies the space shuttle (#1498, src/Bots/ShuttleBotNetProbe): the fixture airport, one
# client (an admin) asks /bot shuttle 200 and sees the server's bot take the parked shuttle, take off, climb into
# Space and switch the cruise drive on. Today's main stops there online (G2: a server body above 60 km stays in the
# flat frame; between bodies needs C3b). SPEED=n (default 4) runs the simulation n times faster.
. "$(dirname "$0")/lib/twoclient.sh" shuttlebotnet
PORT=${PORT:-7883}
tc_server 1500 120 "$OUT/shuttlebotnet_server.log" --server --port $PORT --world fixture --chunks fixture:airport --traffic 0 \
    --no-query --admin-password shuttlebot --botlog
tc_client 1400 "$OUT/shuttlebotnet_A.log" --connect 127.0.0.1:$PORT --name ShuttleA --world fixture --chunks fixture:airport \
    --traffic 0 --shuttlebotnetcheck --checkspeed "${SPEED:-4}"
tc_stop
grep -h "\[bots\] .*shuttle" "$OUT/shuttlebotnet_server.log" | head -30
grep -h "\[shuttlebotnet\] \(ok\|FAIL\)" "$OUT/shuttlebotnet_A.log"
if tc_ok 1 "$OUT/shuttlebotnet_A.log"; then echo "[shuttlebotnetcheck] RESULT: ok"; exit 0; fi
echo "[shuttlebotnetcheck] RESULT: FAILED (see $OUT/shuttlebotnet_*.log)"; exit 1
