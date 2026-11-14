#!/usr/bin/env bash
# The server streams the globe's pyramid to a client that has none (#1656, docs/notes/net/pyramid-streaming.md): a
# dedicated server under --frame earth on B4's leaves and a full pyramid (PYRAMID); client A with the same leaves and
# pyramid on its disk, client B with no leaves, no pyramid (the shipped base alone) and an empty cache. Both climb from
# 2 km to 2,000 km over Lavaux (--globeclimb, src/World/Globe/Globe.Climb.cs), counting the globe's pops on the way.
# B must stream its tiles from the server ("N tiles best from the server", and pyramid entries in its cache), draw the
# same deepest level as A 2.5 altitudes ahead after a hover at the top, and pop no more than A. A streams nothing.
#   GODOT=<exe> [LEAVES=<dir>] [PYRAMID=<dir>] [AT=E,N] [CLIMB=from,to,seconds] [PORT=] tools/pyramidstreamcheck.sh
#   logs: test_output/pyramidstream_*.log
. "$(dirname "$0")/lib/twoclient.sh" pyramidstream
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
PYRAMID=${PYRAMID:-D:/UnitSportSwitzerland/cube_global}
AT=${AT:-2545000,1149400}   # Lavaux, inside B4's leaves
CLIMB=${CLIMB:-2000,2000000,60}
PORT=${PORT:-7895}
if [ ! -d "$LEAVES" ] || [ ! -f "$PYRAMID/cube_manifest.json" ]; then
  echo "[pyramidstreamcheck] no LEAVES or PYRAMID: skipped"; echo "[pyramidstreamcheck] RESULT: ok (skipped)"; exit 0
fi
NOLEAVES="$OUT/pyr_no_leaves"; CACHE="$OUT/pyramidstream_cache"
rm -rf "$NOLEAVES" "$CACHE"; mkdir -p "$NOLEAVES" "$CACHE"
EARTH=(--frame earth "${NOTERRAIN[@]}")
LOOK=(--at "$AT" --origin "$AT" --globeclimb "$CLIMB" --nohud --nocapture --date 2027-07-15 --time 12 --style cartoon
  --systems terrain,generated,physics,sky,ui,network)
tc_server 900 180 "$OUT/pyramidstream_server.log" --server --port $PORT --no-query "${EARTH[@]}" --leaves "$LEAVES" --globe "$PYRAMID" || exit 1
tc_client 900 "$OUT/pyramidstream_A.log" --connect 127.0.0.1:$PORT --name Owner "${EARTH[@]}" --leaves "$LEAVES" --globe "$PYRAMID" "${LOOK[@]}"
( tc_userdata "$(mkdir -p "$OUT/userdata_pyramidstream_B" && cd "$OUT/userdata_pyramidstream_B" && pwd)"
  tc_client 900 "$OUT/pyramidstream_B.log" --connect 127.0.0.1:$PORT --name Streamer "${EARTH[@]}" --leaves "$NOLEAVES" \
    --globe res://assets/globe --cache "$CACHE" "${LOOK[@]}" )
tc_stop
grep -h "\[stream\] pyramid index" "$OUT/pyramidstream_server.log" | head -1 | sed 's/^/server: /'
for c in A B; do grep -h "\[globeclimb\] [0-9]\|\[globeclimb\] deepest\|\[earth\] the server's pyramid\|\[stream\] pyramid index received" "$OUT/pyramidstream_$c.log" | sed "s/^/$c: /"; done
fail=
for c in A B; do grep -q "RESULT PASS globeclimb" "$OUT/pyramidstream_$c.log" || fail="${fail:+$fail; }$c's climb did not finish"; done
grep -q ", 0 tiles best from the server" "$OUT/pyramidstream_A.log" || fail="${fail:+$fail; }A streamed tiles it has"
grep -q ", [1-9][0-9]* tiles best from the server" "$OUT/pyramidstream_B.log" || fail="${fail:+$fail; }B streamed no tile"
# the deepest level 2.5 altitudes ahead after the hover at the top: B has caught up with A (the climb's own levels are
# printed for the lag on the way: a 60 s climb to orbit outruns any stream)
top() { grep -ho "at the top after [0-9]* s: level [0-9-]*" "$1" | grep -o "[0-9-]*$"; }
la=$(top "$OUT/pyramidstream_A.log"); lb=$(top "$OUT/pyramidstream_B.log")
echo "[pyramidstreamcheck] deepest level ahead at the top: A ${la:-?}, B ${lb:-?}"
[ -n "$la" ] && [ "$la" = "$lb" ] || fail="${fail:+$fail; }B drew another level than A at the top"
pops() { grep -ho "RESULT PASS globeclimb: [0-9]* pops" "$1" | grep -o "[0-9]* pops" | grep -o "[0-9]*"; }
pa=$(pops "$OUT/pyramidstream_A.log"); pb=$(pops "$OUT/pyramidstream_B.log")
echo "[pyramidstreamcheck] pops over 2 px: A ${pa:-?}, B ${pb:-?}; B's cache: $(ls "$CACHE" | grep -c '^pyr_') pyramid entries, $(du -sm "$CACHE" | cut -f1) MB"
[ "$(ls "$CACHE" | grep -c '^pyr_')" -gt 0 ] || fail="${fail:+$fail; }B cached no pyramid tile"
[ -n "$pb" ] && [ -n "$pa" ] && [ "$pb" -le $((pa + 2)) ] || fail="${fail:+$fail; }B popped more than A"
if [ -z "$fail" ]; then echo "[pyramidstreamcheck] RESULT: ok"; exit 0; fi
echo "[pyramidstreamcheck] RESULT: FAILED ($fail; see $OUT/pyramidstream_*.log)"; exit 1
