#!/usr/bin/env bash
# Player ids over loopback (#1359, src/Net/PlayerIds + IdsNetProbe): a dedicated server on the flat fixture starts with
# files from before ids, keyed by the name "Anna": user://admins.json lists her and user://bank/accounts.json holds
# 500 CHF for her. Client ANNA (her own user://, so her own PlayerKey) joins as Anna: she must claim both. She comes
# back as Zed with the same user://: both must follow her id. IMPOSTOR (another user://) joins as Anna: nothing.
# Then the files must hold her id, not the name. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/idsnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" ids
PORT=${PORT:-7891}
WORLD="--world fixture --chunks fixture:flat"
# the server's user:// inside the harness's folder (APPDATA/Godot on Windows, XDG_DATA_HOME/godot on Linux, HOME on macOS)
NAME=$(sed -n 's/^config\/name="\(.*\)"/\1/p' project.godot | head -1)
case "$(uname)" in
  Darwin) UD="$OUT/userdata_ids/Library/Application Support/Godot/app_userdata/$NAME" ;;
  Linux) UD="$OUT/userdata_ids/godot/app_userdata/$NAME" ;;
  *) UD="$OUT/userdata_ids/Godot/app_userdata/$NAME" ;;
esac
mkdir -p "$UD/bank"
echo '["Anna"]' > "$UD/admins.json"
echo '{"Anna": 500}' > "$UD/bank/accounts.json"

tc_server 300 120 "$OUT/ids_server.log" --server --port $PORT $WORLD
A_UD="$OUT/userdata_ids_anna"; rm -rf "$A_UD"; mkdir -p "$A_UD"
I_UD="$OUT/userdata_ids_impostor"; rm -rf "$I_UD"; mkdir -p "$I_UD"
( tc_userdata "$A_UD"; tc_client 150 "$OUT/ids_anna.log" --connect 127.0.0.1:$PORT --name Anna $WORLD --idscheck anna )
( tc_userdata "$A_UD"; tc_client 150 "$OUT/ids_zed.log" --connect 127.0.0.1:$PORT --name Zed $WORLD --idscheck zed )
( tc_userdata "$I_UD"; tc_client 150 "$OUT/ids_impostor.log" --connect 127.0.0.1:$PORT --name Anna $WORLD --idscheck impostor )
tc_stop
grep -h "\[ids" "$OUT/ids_anna.log" "$OUT/ids_zed.log" "$OUT/ids_impostor.log" "$OUT/ids_server.log"
grep -h "\[admin\]\|\[bank\]" "$OUT/ids_server.log"
ok=1
tc_ok 3 "$OUT/ids_anna.log" "$OUT/ids_zed.log" "$OUT/ids_impostor.log" || ok=0
grep -q "the admin rights of Anna now follow their id" "$OUT/ids_server.log" || { echo "[idscheck] no admin claim in the server log"; ok=0; }
grep -q "account of Anna now kept under" "$OUT/ids_server.log" || { echo "[idscheck] no bank claim in the server log"; ok=0; }
# the files: Anna's id, no name key left
grep -q '"Anna"' "$UD/bank/accounts.json" && { echo "[idscheck] accounts.json still keyed by the name: $(cat "$UD/bank/accounts.json")"; ok=0; }
grep -q '"names": \[\]' "$UD/admins.json" || { echo "[idscheck] admins.json still lists a name: $(cat "$UD/admins.json")"; ok=0; }
if [ $ok = 1 ]; then echo "[idscheck] RESULT: ok"; exit 0; fi
echo "[idscheck] RESULT: FAILED (see $OUT/ids_*.log)"; exit 1
