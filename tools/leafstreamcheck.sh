#!/usr/bin/env bash
# The server streams cube leaves to a client that has none (#1655, G2 C7, docs/notes/net/leaf-streaming.md): a
# dedicated server under --frame earth on B4's built leaves; client A joins with the same leaves on its disk, client B
# with no leaves, no tiles and an empty cache. Both run --earthcheck (src/Terrain/Earth/EarthCheck) at Geneva: the
# ground under the start, collision against the height queries, a dropped ball, the ground streaming ahead of a body
# moving TRAVEL m, the leaves furnished (roads, buildings, doors). B (the generated fill off: no ground but the server's)
# must get every leaf from the server (its log:
# "streamed on arrival: N MB, M leaves from the server's index, 0 on this copy's disk"), A none.
# Then B runs again on the cache it filled, the fill on: the fill stands first and the real leaves replace it in place
# when the index lands (EarthWorld.Refresh), nothing streamed but the index. Skipped without the leaves.
#   GODOT=<exe> [LEAVES=<dir>] [AT=E,N] [TRAVEL=m] [PORT=] tools/leafstreamcheck.sh      logs: test_output/leafstream_*.log
. "$(dirname "$0")/lib/twoclient.sh" leafstream
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
AT=${AT:-2500956,1117569}   # Geneva, the densest leaf of the D: region
TRAVEL=${TRAVEL:-4500}
PORT=${PORT:-7891}
if [ ! -d "$LEAVES" ]; then echo "[leafstreamcheck] no LEAVES with built leaves: skipped"; echo "[leafstreamcheck] RESULT: ok (skipped)"; exit 0; fi
NOLEAVES="$OUT/no_leaves"; CACHE="$OUT/leafstream_cache"
rm -rf "$NOLEAVES" "$CACHE"; mkdir -p "$NOLEAVES" "$CACHE"
EARTH=(--frame earth "${NOTERRAIN[@]}")
CHECK=(--earthcheck --at "$AT" --earthtravel "$TRAVEL")
tc_server 900 180 "$OUT/leafstream_server.log" --server --port $PORT --no-query "${EARTH[@]}" --leaves "$LEAVES" || exit 1
tc_client 600 "$OUT/leafstream_A.log" --connect 127.0.0.1:$PORT --name Owner "${EARTH[@]}" --leaves "$LEAVES" "${CHECK[@]}" &
A=$!
( tc_userdata "$(mkdir -p "$OUT/userdata_leafstream_B" && cd "$OUT/userdata_leafstream_B" && pwd)"
  tc_client 600 "$OUT/leafstream_B.log" --connect 127.0.0.1:$PORT --name Streamer "${EARTH[@]}" --leaves "$NOLEAVES" --cache "$CACHE" "${CHECK[@]}" --generated off )
wait $A
# B again, from the cache it filled
( tc_userdata "$OUT/userdata_leafstream_B"
  tc_client 600 "$OUT/leafstream_B2.log" --connect 127.0.0.1:$PORT --name Streamer "${EARTH[@]}" --leaves "$NOLEAVES" --cache "$CACHE" "${CHECK[@]}" )
tc_stop
for c in A B B2; do grep -h "\[earthcheck\] \(streamed\|ground under\|RESULT\|FAIL\)\|\[earth\] the server's leaves" "$OUT/leafstream_$c.log" | sed "s/^/$c: /"; done
grep -h "\[stream\] leaf index" "$OUT/leafstream_B.log" | head -1
fail=
tc_ok 3 "$OUT"/leafstream_A.log "$OUT"/leafstream_B.log "$OUT"/leafstream_B2.log || fail="a check failed"
# A streams no leaf, B gets them all from the server
grep -q "streamed in all: .*, 0 leaves from the server's index" "$OUT/leafstream_A.log" || fail="${fail:+$fail; }A took leaves from the server"
grep -q "streamed on arrival: .*, [1-9][0-9]* leaves from the server's index, 0 on this copy's disk" "$OUT/leafstream_B.log" || fail="${fail:+$fail; }B did not get its leaves from the server"
cached=$(ls "$CACHE" | grep -c "^leaf_")
echo "[leafstreamcheck] B's cache: $cached leaf entries, $(du -sm "$CACHE" | cut -f1) MB"
[ "$cached" -gt 0 ] || fail="${fail:+$fail; }nothing cached"
if [ -z "$fail" ]; then echo "[leafstreamcheck] RESULT: ok"; exit 0; fi
echo "[leafstreamcheck] RESULT: FAILED ($fail; see $OUT/leafstream_*.log)"; exit 1
