#!/usr/bin/env bash
# The dressing services on an earth server over real leaves (#1704, src/Terrain/Earth/DressCheck): B4's region on D:,
# a server and one client under --frame earth at each stop, both counting what the services placed round it (parked
# fleets, airport stands, herds, birds, the Nyon steamer) and checking it stands on the leaves' ground. Skipped
# without the leaves. One stop at a time (a server and a client, ~3 GB): about 3-4 min a stop.
#   GODOT=<exe> [LEAVES=] [CHUNKS=] [STOPS="geneva nyon airport riddes"] tools/earthdresscheck.sh
#   output in test_output/dress_<stop>_{server,client}.log
. "$(dirname "$0")/lib/twoclient.sh" dress
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
CHUNKS=${CHUNKS:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks}
PORT=${PORT:-7917}
STOPS=${STOPS:-geneva nyon airport riddes}
if [ ! -d "$LEAVES" ] || [ ! -d "$CHUNKS" ]; then echo "[earthdresscheck] no LEAVES/CHUNKS on D:: skipped"; echo "[earthdresscheck] RESULT: ok (skipped)"; exit 0; fi
fail=

stop() { # name at expect
  local name=$1 at=$2 expect=$3
  local world=(--frame earth --leaves "$LEAVES" --chunks "$CHUNKS" --traffic 0 --dresscheck "$at" --dressexpect "$expect")
  tc_server 400 180 "$OUT/dress_${name}_server.log" --server --port $PORT --no-query "${world[@]}" \
    || { fail="${fail:+$fail; }$name: the server did not start"; return; }
  tc_client 400 "$OUT/dress_${name}_client.log" --connect 127.0.0.1:$PORT --name Dress --at "$at" "${world[@]}" &
  local client=$! i
  # both print their RESULT once their counts are in (the server's round the client's body): the client stays until both have
  for i in $(seq 1 2000); do
    [ "$(grep -h "\[dresscheck .*RESULT" "$OUT/dress_${name}_server.log" "$OUT/dress_${name}_client.log" 2>/dev/null | wc -l)" -ge 2 ] && break
    kill -0 "$client" 2>/dev/null || break
    guard_nap 0.2
  done
  tc_stop "$client"
  tc_stop
  grep -h "\[dresscheck" "$OUT/dress_${name}_server.log" "$OUT/dress_${name}_client.log" | sed "s/^/$name: /"
  local ok
  ok=$(grep -h "\[dresscheck .*RESULT: ok" "$OUT/dress_${name}_server.log" "$OUT/dress_${name}_client.log" | wc -l)
  [ "$ok" -ge 2 ] || fail="${fail:+$fail; }$name"
}

for s in $STOPS; do
  case $s in
    geneva)  stop geneva  2500956,1117569 fleet,birds ;;
    nyon)    stop nyon    2508092,1137444 steamer ;;
    airport) stop airport 2497653,1121360 stands ;;
    riddes)  stop riddes  2581500,1113300 herds ;;
  esac
done
if [ -z "$fail" ]; then echo "[earthdresscheck] RESULT: ok"; exit 0; fi
echo "[earthdresscheck] RESULT: FAILED ($fail; see $OUT/dress_*.log)"; exit 1
