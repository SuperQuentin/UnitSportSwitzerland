#!/usr/bin/env bash
# The earth server builds the clients' fill (#1682, G2 D gap): a dedicated server and a client under --frame earth read
# the ground's ellipsoidal height at the same 5 x 5 Earth points 150 m apart (--fillagree, src/Terrain/Earth/FillAgreeProbe)
# and must agree to the centimetre on every point, the fill's leaves among them.
#   1. the generated world (--generated-world, NOTERRAIN): every point on the fill;
#   2. with LEAVES (B4's built leaves on D:, skipped without them): the fill past the region's edge, where it blends into
#      the real leaves (some points real, some fill).
#   3. pyramid (#1656, with LEAVES and PYRAMID): the server has the globe's pyramid on its disk, the client none (the
#      shipped base alone): the client's fill must stand on the server's pyramid, streamed, and agree as in 2.
#   GODOT=<exe> [RUNS="generated edge pyramid"] [AT=E,N] [LEAVES=<dir>] [PYRAMID=<dir>] [EDGE=E,N] [PORT=] tools/fillagreenetcheck.sh
#   logs: test_output/fillagree_*.log
. "$(dirname "$0")/lib/twoclient.sh" fillagree
AT=${AT:-2600500,1199500}
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
EDGE=${EDGE:-2499550,1112700}   # across the south-west edge of B4's Geneva leaves: 20 points fill, 5 real
PYRAMID=${PYRAMID:-D:/UnitSportSwitzerland/cube_global}
SERVER_EXTRA=(); CLIENT_EXTRA=()
PORT=${PORT:-7893}
fail=

compare() { # name: the server's and the client's points, line by line
  local s="$OUT/fillagree_$1_server.log" c="$OUT/fillagree_$1_client.log"
  grep -h "RESULT: FAILED" "$s" "$c" && { fail="${fail:+$fail; }$1: a side had no ground"; return; }
  grep -h "points on the fill" "$s" "$c" | sed "s/^/$1: /"
  local out
  out=$(paste <(grep -o "\[fillagree server\] point .*" "$s" | awk '{print $4, $5, $6}') \
              <(grep -o "\[fillagree client\] point .*" "$c" | awk '{print $4, $5, $6}') | awk -v n="$1" '
    NF == 6 { rows++; d = $3 - $6; if (d < 0) d = -d; if (d > worst) worst = d; if ($2 == "fill") fill++; else real++
              if ($1 != $4 || $2 != $5 || d > 0.01) { bad++; print n ": point " $1 " server " $2 " " $3 " vs client " $5 " " $6 } }
    END { printf "%s: %d points compared, %d on the fill, %d real, worst %.3f m\n", n, rows, fill, real, worst + 0
          if (rows != 25 || bad > 0 || fill == 0 || (n == "edge" && real == 0)) print "MISMATCH" }')
  echo "$out"
  { grep -q MISMATCH <<< "$out" || ! grep -q "points compared" <<< "$out"; } && fail="${fail:+$fail; }$1: the server and the client disagree"
}

run() { # name at args...
  local name=$1 at=$2; shift 2
  tc_server 600 180 "$OUT/fillagree_${name}_server.log" --server --port $PORT --no-query --frame earth --player-status \
    --fillagree "$at" "$@" ${SERVER_EXTRA[@]+"${SERVER_EXTRA[@]}"} || { fail="${fail:+$fail; }$name: the server did not start"; return; }
  tc_client 600 "$OUT/fillagree_${name}_client.log" --connect 127.0.0.1:$PORT --name Agree --frame earth --at "$at" --fillagree "$at" "$@"     ${CLIENT_EXTRA[@]+"${CLIENT_EXTRA[@]}"} &
  local client=$! i
  # both print once their leaves round the client's body are in (the server's only while that body is there): the
  # client stays until both have
  for i in $(seq 1 3000); do
    [ "$(grep -h "points on the fill\|RESULT: FAILED" "$OUT/fillagree_${name}_server.log" "$OUT/fillagree_${name}_client.log" 2>/dev/null | wc -l)" -ge 2 ] && break
    kill -0 "$client" 2>/dev/null || break
    guard_nap 0.2
  done
  tc_stop "$client"
  tc_stop
  compare "$name"
}

RUNS=${RUNS:-generated edge pyramid}   # RUNS=edge: the region's edge alone
[[ " $RUNS " == *" generated "* ]] && run generated "$AT" --generated-world "${NOTERRAIN[@]}"
if [[ " $RUNS " == *" edge "* ]]; then
  if [ -d "$LEAVES" ]; then run edge "$EDGE" "${NOTERRAIN[@]}" --leaves "$LEAVES"
  else echo "[fillagreenetcheck] no LEAVES: the region's edge skipped"; fi
fi
if [[ " $RUNS " == *" pyramid "* ]]; then
  if [ -d "$LEAVES" ] && [ -f "$PYRAMID/cube_manifest.json" ]; then
    SERVER_EXTRA=(--globe "$PYRAMID"); CLIENT_EXTRA=(--globe res://assets/globe --cache "$OUT/fillagree_pyramid_cache")
    rm -rf "$OUT/fillagree_pyramid_cache"; mkdir -p "$OUT/fillagree_pyramid_cache"
    run pyramid "$EDGE" "${NOTERRAIN[@]}" --leaves "$LEAVES"
    SERVER_EXTRA=(); CLIENT_EXTRA=()
    grep -h "\[earth\] the fill on\|\[earth\] the server's pyramid" "$OUT/fillagree_pyramid_server.log" "$OUT/fillagree_pyramid_client.log" | sed 's/^/pyramid: /'
    grep -q "\[earth\] the server's pyramid: [1-9][0-9]* tiles.*the fill on it" "$OUT/fillagree_pyramid_client.log"       || fail="${fail:+$fail; }pyramid: the client's fill is not on the server's pyramid"
  else echo "[fillagreenetcheck] no LEAVES or PYRAMID: the streamed pyramid skipped"; fi
fi

if [ -z "$fail" ]; then echo "[fillagreenetcheck] RESULT: ok"; exit 0; fi
echo "[fillagreenetcheck] RESULT: FAILED ($fail; see $OUT/fillagree_*.log)"; exit 1
