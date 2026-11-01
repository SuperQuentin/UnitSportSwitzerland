#!/usr/bin/env bash
# Birthdays over loopback (#1423, src/Occasions/Birthdays + BirthdayNetProbe): a dedicated server on the flat fixture on a fixed
# date, Tom (guest, in first, its own user://) and Lea (celebrant: --birthday today with a year, shown). Tom hears the chat line
# with Lea's age and sees her as a celebrant; he stands by her and wishes her a happy birthday: Lea reads it, both see the
# paper, no second wish is offered, Lea's /birthday counts one; Lea lights birthday fireworks and Tom's sky plays the same show.
#   GODOT=<exe> [PORT=] tools/birthdaynetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" birthdaynet
PORT=${PORT:-7936}
DATE="--date 2026-11-01"
WORLD="--world fixture --chunks fixture:flat"
CLIENT="--systems network,physics,ui,occasions $DATE"
RESULT=ok

: > "$OUT/birthdaynet_A.log"; : > "$OUT/birthdaynet_B.log"
tc_server 600 120 "$OUT/birthdaynet_server.log" --server --port $PORT $WORLD --systems network,sky $DATE
B_UD="$OUT/userdata_birthdaynet_B"; rm -rf "$B_UD"; mkdir -p "$B_UD"
( tc_userdata "$B_UD"; tc_client 420 "$OUT/birthdaynet_B.log" --connect 127.0.0.1:$PORT --name Tom $WORLD $CLIENT --birthdaynet guest ) &
B=$!
# Lea joins once Tom listens to the chat: the birthday's line is said as she arrives
for _ in $(seq 1 300); do grep -q "\[birthdaynet GUEST\] listening" "$OUT/birthdaynet_B.log" 2>/dev/null && break; sleep 0.5; done
tc_client 420 "$OUT/birthdaynet_A.log" --connect 127.0.0.1:$PORT --name Lea $WORLD $CLIENT --birthday 01-11-1996 --birthday-public --birthdaynet celebrant &
A=$!
wait $A $B
tc_stop
grep -h "\[birthday\]\|\[birthdaynet" "$OUT/birthdaynet_server.log" "$OUT/birthdaynet_A.log" "$OUT/birthdaynet_B.log" | grep -v " ok " | head -60
tc_ok 2 "$OUT/birthdaynet_A.log" "$OUT/birthdaynet_B.log" && grep -q "wished Lea a happy birthday" "$OUT/birthdaynet_server.log" || RESULT=FAILED

if [ "$RESULT" = ok ]; then echo "[birthdaynetcheck] RESULT: ok"; exit 0; fi
echo "[birthdaynetcheck] RESULT: FAILED (see $OUT/birthdaynet_*.log)"; exit 1
