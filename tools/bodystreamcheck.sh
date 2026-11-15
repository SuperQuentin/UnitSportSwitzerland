#!/usr/bin/env bash
# The server streams another body's pyramid to a client that has only the shipped base (#1849,
# docs/notes/net/pyramid-streaming.md "Other bodies"): a dedicated server under --frame earth with the bodies' local install
# (BODIES, each <body>/cube), and client B with no body data (an empty --bodies) and an empty cache. B lands on the Moon from
# orbit (--mooncheck --fromorbit) and must: receive the Moon's index ("the server's pyramid for Moon"), deepen its globe
# past the shipped base's level 2, stand on ground read at the server's finest level, and cache the Moon's tiles (pyr1_*).
#   GODOT=<exe> [BODIES=<dir>] [BODY=moon] [PORT=] tools/bodystreamcheck.sh
#   logs: test_output/bodystream_*.log
. "$(dirname "$0")/lib/twoclient.sh" bodystream
BODIES=${BODIES:-E:/UnitSportSwitzerland/planets}
BODY=${BODY:-moon}
PORT=${PORT:-7897}
if [ ! -f "$BODIES/$BODY/cube/cube_manifest.json" ]; then
  echo "[bodystreamcheck] no BODIES/$BODY/cube: skipped"; echo "[bodystreamcheck] RESULT: ok (skipped)"; exit 0
fi
NOBODIES="$OUT/bodystream_no_bodies"; CACHE="$OUT/bodystream_cache"
rm -rf "$NOBODIES" "$CACHE"; mkdir -p "$NOBODIES" "$CACHE"
EARTH=(--frame earth "${NOTERRAIN[@]}")
tc_server 900 300 "$OUT/bodystream_server.log" --server --port $PORT --no-query "${EARTH[@]}" --bodies "$BODIES" || exit 1
( tc_userdata "$(mkdir -p "$OUT/userdata_bodystream_B" && cd "$OUT/userdata_bodystream_B" && pwd)"
  tc_client 900 "$OUT/bodystream_B.log" --connect 127.0.0.1:$PORT --name Lander "${EARTH[@]}" --bodies "$NOBODIES" \
    --cache "$CACHE" --sun real --date 2027-07-15 --time 10 --occasion none \
    --mooncheck $( [ "$BODY" = moon ] || echo "$BODY" ) --fromorbit )
tc_stop
NAME=$(echo "${BODY:0:1}" | tr a-z A-Z)${BODY:1}
grep -h "\[stream\] body .*pyramid index" "$OUT/bodystream_server.log" | head -3 | sed 's/^/server: /'
grep -h "\[bodies\] $NAME's pyramid\|the server's pyramid for $NAME\|$NAME's ground now\|standing at\|RESULT" "$OUT/bodystream_B.log" | sed 's/^/B: /'
fail=
grep -q "\[bodies\] $NAME's pyramid: the shipped base" "$OUT/bodystream_B.log" || fail="${fail:+$fail; }B did not start on the shipped base alone"
grep -q "the server's pyramid for $NAME: [1-9]" "$OUT/bodystream_B.log" || fail="${fail:+$fail; }B got no index for $NAME"
grep -q "\[mooncheck\] RESULT: ok" "$OUT/bodystream_B.log" || fail="${fail:+$fail; }B's landing failed"
lvl=$(grep -ho "read at level [0-9]*" "$OUT/bodystream_B.log" | tail -1 | grep -o "[0-9]*$")
echo "[bodystreamcheck] B stood on ground read at level ${lvl:-?}; its cache: $(ls "$CACHE" | grep -c '^pyr[0-9]') body entries"
[ -n "$lvl" ] && [ "$lvl" -gt 2 ] || fail="${fail:+$fail; }B's ground did not read past the shipped base's level 2"
[ "$(ls "$CACHE" | grep -c '^pyr[0-9]')" -gt 0 ] || fail="${fail:+$fail; }B cached no body tile"
if [ -z "$fail" ]; then echo "[bodystreamcheck] RESULT: ok"; exit 0; fi
echo "[bodystreamcheck] RESULT: FAILED ($fail; see $OUT/bodystream_*.log)"; exit 1
