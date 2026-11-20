#!/usr/bin/env bash
# A bot on a dedicated server flies the space shuttle (#1498, src/Bots/ShuttleBotNetProbe): the fixture airport, one
# client (an admin) asks /bot shuttle 200 and sees the server's bot take the parked shuttle, take off and climb away
# out of its sight; the server's log must show it in Space with the cruise drive on (Climb -> DriveUp). Today's main
# stops there online: round the Earth a server body stays in the flat LV95 frame, wrong far from Switzerland (G2,
# #1443), and between bodies needs C3b (#1975). SPEED=n (default 8) runs the simulation n times faster.
. "$(dirname "$0")/lib/twoclient.sh" shuttlebotnet
PORT=${PORT:-7883}
tc_server 1500 120 "$OUT/shuttlebotnet_server.log" --server --port $PORT --world fixture --chunks fixture:airport --traffic 0 \
    --no-query --admin-password shuttlebot --botlog
tc_client 1400 "$OUT/shuttlebotnet_A.log" --connect 127.0.0.1:$PORT --name ShuttleA --world fixture --chunks fixture:airport \
    --traffic 0 --shuttlebotnetcheck --checkspeed "${SPEED:-8}"
tc_stop
grep -h "\[bots\] .*shuttle" "$OUT/shuttlebotnet_server.log" | head -30
grep -h "\[shuttlebotnet\] \(ok\|FAIL\)" "$OUT/shuttlebotnet_A.log"
space=$(grep -c "shuttle: Climb -> DriveUp" "$OUT/shuttlebotnet_server.log")
[ "$space" -gt 0 ] && echo "[shuttlebotnetcheck] ok   the server's bot in Space, the cruise drive on" || echo "[shuttlebotnetcheck] FAIL the server's bot never reached Space with the drive on"
if [ "$space" -gt 0 ] && tc_ok 1 "$OUT/shuttlebotnet_A.log"; then echo "[shuttlebotnetcheck] RESULT: ok"; exit 0; fi
echo "[shuttlebotnetcheck] RESULT: FAILED (see $OUT/shuttlebotnet_*.log)"; exit 1
