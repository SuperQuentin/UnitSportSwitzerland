#!/usr/bin/env bash
# Aircraft at the airport stands over loopback (#422, src/World/AirportStands + AirportCheck): a dedicated
# server on the fixture airport places the six A320s with their airstairs, the AN-124 and the freighter;
# a headless client sees them all (stairs docked on its own copy, L1 and L2 open), takes an A320, sees
# its stand stay empty while it stands near, and refilled with its stairs once it is 1.5 km away (the
# server refills after --standrespawn 4 s). No terrain data needed. Output in test_output/airportnet_*.log.
#   GODOT=<exe> [PORT=] tools/airportnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" airportnet
PORT=${PORT:-7871}
tc_server 400 120 "$OUT/airportnet_server.log" --server --port $PORT --world fixture --chunks fixture:airport --traffic 0 --standrespawn 4
tc_client 300 "$OUT/airportnet_A.log" --connect 127.0.0.1:$PORT --name AirportA --world fixture --chunks fixture:airport --traffic 0 --airportcheck
tc_stop
grep -h "\[airports\]" "$OUT/airportnet_server.log"
grep -h "\[airportcheck\] \(ok\|FAIL\)" "$OUT/airportnet_A.log"
if tc_ok 1 "$OUT/airportnet_A.log"; then echo "[airportnetcheck] RESULT: ok"; exit 0; fi
echo "[airportnetcheck] RESULT: FAILED (see $OUT/airportnet_*.log)"; exit 1
