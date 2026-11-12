#!/usr/bin/env bash
# The long drive (#1710, G2 D0; #1744; src/Player/LongDriveCheck): a car driven from Geneva toward Nyon on the D: region,
# on one route planned on the lane graph of the corridor (the leaves' roads under earth, the LV95 tiles' under flat; the
# LV95 tiles' in both for the parity runs), at
# each road's speed, re-anchoring every 500 m (--originshift).
#
# The bar is parity (#1744, the user's call): real roads stop the drive at places the frame has nothing to do with (the
# Geneva blockers filed from #1744, on flat and earth alike), so the drive runs on flat, then on earth, on the same route,
# and earth passes when the first place it was stopped (its first blocked place, or where it ended if nothing stopped it)
# is flat's (within PARITY_M) and it had no re-anchor fault of its own (a jump, the ground moving) over the whole run.
# What follows a first stop (backing off, planning round it) is chaotic: centimetres apart there, the two runs part
# (measured: the same first two blocked places, then 60 m apart, then one crashed and one gave up); it is logged, not
# judged. FRAME=flat|earth runs one frame alone (its own RESULT); COMPARE_ONLY=1 judges the last two logs again.
# Real time (the leaves stream on threads): each run until the drive ends, ~10-60 min.
#   GODOT=<exe> [FRAME=flat|earth] [LEAVES=dir] [CHUNKS=dir] [AT=E,N] [TO=E,N] [CAR=n] [WINDOWED=1] tools/longdrivecheck.sh
# output: test_output/longdrivecheck.log (one run), longdrivecheck_flat.log and longdrivecheck_earth.log (parity)
cd "$(dirname "$0")/.." || exit 1
NAME=longdrivecheck
LEAVES=${LEAVES:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks_cube}
CHUNKS=${CHUNKS:-D:/UnitSportSwitzerland/test_b3/b4/terrain_chunks}
AT=${AT:-2500700,1122300}   # Geneva, Bellevue by the A1 (the farthest the drive got before a blocker, 3.7 km)
TO=${TO:-2507500,1138500}   # Nyon, the Geneva patch's north end along the lake
NEED=("$LEAVES")
TIMEOUT_S=${TIMEOUT_S:-3600}
PARITY_M=${PARITY_M:-60}
ARGS=(--longdrivecheck --leaves "$LEAVES" --chunks "$CHUNKS" --at "$AT" --to "$TO" --car "${CAR:-4}" --traffic 0
  --originshift "${SHIFT:-500}" --systems "${SYSTEMS:-terrain,physics,sky}")
[ -n "${AVOID:-}" ] && ARGS+=(--avoid "$AVOID")
# shellcheck disable=SC2206
[ -n "${EXTRA:-}" ] && ARGS+=($EXTRA)   # more probe arguments (a trial), as words

if [ -n "${FRAME:-}" ]; then . tools/lib/barprobe.sh; fi   # one frame alone: barprobe exits with its result

for d in "${NEED[@]}"; do
  if [ -z "${COMPARE_ONLY:-}" ] && [ ! -d "$d" ]; then echo "[$NAME] no $d on this machine: skipped"; echo "[$NAME] RESULT: ok (skipped: no data)"; exit 0; fi
done
# parity: flat, then earth, each a barprobe run of its own (a subshell: barprobe exits), both planning on the LV95 road
# tiles (the earth world reads them through its chunk source, in its own frame): the same route, so what differs is the
# frame's (the leaves' own roads are another graph, cut and joined otherwise: their route differs from the first km)
ARGS+=(--routeroads lv95)
if [ -z "${COMPARE_ONLY:-}" ]; then
  for f in flat earth; do
    ( FRAME=$f; . tools/lib/barprobe.sh )
    cp test_output/$NAME.log "test_output/${NAME}_$f.log" 2>/dev/null
  done
fi

# the parity lines of a run: "blocked E/N", "end KIND E/N", "frame TEXT"
lines() { grep -a "^\[$NAME\] parity " "test_output/${NAME}_$1.log" 2>/dev/null | sed "s/^\[$NAME\] parity //" | tr -d '\r'; }
near() { # E/N E/N: within PARITY_M
  awk -v a="$1" -v b="$2" -v m="$PARITY_M" 'BEGIN { split(a, p, "/"); split(b, q, "/"); d = sqrt((p[1]-q[1])^2 + (p[2]-q[2])^2); exit !(d <= m) }'
}
fail=()
flat_end=$(lines flat | awk '$1 == "end"'); earth_end=$(lines earth | awk '$1 == "end"')
[ -n "$flat_end" ] || fail+=("the flat run did not end the drive (see ${NAME}_flat.log)")
[ -n "$earth_end" ] || fail+=("the earth run did not end the drive (see ${NAME}_earth.log)")
while IFS= read -r l; do [ -n "$l" ] && fail+=("earth's own re-anchor fault: ${l#frame }"); done < <(lines earth | awk '$1 == "frame"')
if [ -n "$flat_end" ] && [ -n "$earth_end" ]; then
  read -r _ fk fp <<< "$flat_end"; read -r _ ek ep <<< "$earth_end"
  mapfile -t fb < <(lines flat | awk '$1 == "blocked" { print $2 }')
  mapfile -t eb < <(lines earth | awk '$1 == "blocked" { print $2 }')
  echo "[$NAME] parity: flat ended $fk at $fp, blocked at ${fb[*]:-none}; earth ended $ek at $ep, blocked at ${eb[*]:-none}"
  # the first stop: the first blocked place, or the end with none
  fs=${fb[0]:-"end:$fk:$fp"}; es=${eb[0]:-"end:$ek:$ep"}
  echo "[$NAME] parity: the first stop on flat $fs, on earth $es"
  if [[ $fs == end:* || $es == end:* ]]; then
    [ "$fs" = "$es" ] || { [[ $fs == end:* && $es == end:* ]] && [ "$fk" = "$ek" ] && near "$fp" "$ep"; }       || fail+=("the first stop differs: flat $fs, earth $es")
  else
    near "$fs" "$es" || fail+=("the first stop differs: flat at $fs, earth at $es")
  fi
fi
if [ ${#fail[@]} -eq 0 ]; then
  echo "[$NAME] RESULT: ok (parity: earth drove as flat did)"
  exit 0
fi
for x in "${fail[@]}"; do echo "[$NAME] parity FAULT: $x"; done
echo "[$NAME] RESULT: FAILED (parity: ${fail[0]})"
exit 1
