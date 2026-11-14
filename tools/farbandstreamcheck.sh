#!/usr/bin/env bash
# The far band furnished for a client that streams its leaves (#1657 phase 2, docs/notes/terrain/far-features.md): a
# dedicated server under --frame earth on B4's leaves and a pyramid; client A with the leaves on its disk, client B with
# none and an empty cache. Both take the same shots over Geneva from 300 m and 800 m (the far band within
# Globe.StreamedFarLeafM, 4 km), into test_output/farband/{A,B}. B must stream far-band sections (cover, trees, water,
# buildings: `leaf_*.s<kind>` entries in its cache beyond the heights) and write every shot; the pictures are for the eye.
#   GODOT=<exe> [LEAVES=<dir>] [PYRAMID=<dir>] [AT=E,N] [PORT=] tools/farbandstreamcheck.sh      logs: test_output/farband_*.log
. "$(dirname "$0")/lib/twoclient.sh" farband
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
PYRAMID=${PYRAMID:-D:/UnitSportSwitzerland/cube_global}
AT=${AT:-2500956,1117569}   # Geneva
PORT=${PORT:-7897}
if [ ! -d "$LEAVES" ] || [ ! -f "$PYRAMID/cube_manifest.json" ]; then
  echo "[farbandstreamcheck] no LEAVES or PYRAMID: skipped"; echo "[farbandstreamcheck] RESULT: ok (skipped)"; exit 0
fi
NOLEAVES="$OUT/farband_no_leaves"; CACHE="$OUT/farband_cache"; SHOTS="$OUT/farband"
rm -rf "$NOLEAVES" "$CACHE" "$SHOTS"; mkdir -p "$NOLEAVES" "$CACHE" "$SHOTS/A" "$SHOTS/B"
EARTH=(--frame earth "${NOTERRAIN[@]}")
queue() { # dir
  printf '0,g300,0,-8,0,8,%s/a_300m.png\n0,g800,0,-14,90,8,%s/b_800m.png\nquit\n' "$1" "$1" > "$1/queue.txt"
}
queue "$SHOTS/A"; queue "$SHOTS/B"
# buildings out to 3 rings (3.5 km, Medium's): the test profile's Low reaches 1.5 km, about where the near leaves end
LOOK=(--buildingrings 3 --at "$AT" --origin "$AT" --nohud --nocapture --date 2027-07-15 --time 12 --style cartoon --systems terrain,generated,physics,sky,ui,network)
tc_server 900 180 "$OUT/farband_server.log" --server --port $PORT --no-query "${EARTH[@]}" --leaves "$LEAVES" --globe "$PYRAMID" || exit 1
tc_client 900 "$OUT/farband_A.log" --windowed --connect 127.0.0.1:$PORT --name Owner "${EARTH[@]}" --leaves "$LEAVES" --globe "$PYRAMID" \
  "${LOOK[@]}" --shot-queue "$SHOTS/A/queue.txt"
( tc_userdata "$(mkdir -p "$OUT/userdata_farband_B" && cd "$OUT/userdata_farband_B" && pwd)"
  tc_client 900 "$OUT/farband_B.log" --windowed --connect 127.0.0.1:$PORT --name Streamer "${EARTH[@]}" --leaves "$NOLEAVES" \
    --globe res://assets/globe --cache "$CACHE" "${LOOK[@]}" --shot-queue "$SHOTS/B/queue.txt" )
tc_stop
fail=
for c in A B; do
  n=$(grep -c "\[shot\] wrote" "$OUT/farband_$c.log")
  echo "[farbandstreamcheck] $c: $n of 2 shots"
  [ "$n" -eq 2 ] || fail="${fail:+$fail; }$c wrote $n of 2 shots"
done
# the far band's own sections (4 water, 2 cover, 7 trees, 6 buildings: LeafKind), beyond the whole near leaves and heights
far=$(ls "$CACHE" | grep -cE '^leaf_.*\.s(2|4|6|7)\.culf$')
echo "[farbandstreamcheck] B's cache: $far far-band sections, $(ls "$CACHE" | grep -c '^leaf_') leaf entries in all, $(du -sm "$CACHE" | cut -f1) MB"
[ "$far" -gt 0 ] || fail="${fail:+$fail; }B streamed no far-band section"
buildings=$(ls "$CACHE" | grep -cE '^leaf_.*\.s6\.culf$')
echo "[farbandstreamcheck] B's far buildings: $buildings leaves, $(ls "$CACHE" | grep -E '^leaf_.*\.s6\.culf$' | (cd "$CACHE" && xargs -r du -cb | tail -1 | cut -f1) | awk '{printf "%.1f", $1 / 1048576}') MB"
[ "$buildings" -gt 0 ] || fail="${fail:+$fail; }B streamed no far buildings"
if [ -z "$fail" ]; then echo "[farbandstreamcheck] RESULT: ok"; exit 0; fi
echo "[farbandstreamcheck] RESULT: FAILED ($fail; see $OUT/farband_*.log)"; exit 1
