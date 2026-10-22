#!/usr/bin/env bash
# Online accounts over loopback (#1123, src/Net/Accounts, docs/notes/net/accounts.md), on a throwaway Nakama
# (tools/nakama/throwaway.sh, so it needs Docker running):
# - a server with --accounts required refuses an offline client with the reason, and admits one signed in
#   (--account-email/--account-password, the account made on the fly);
# - a server with --accounts optional admits the offline client, as an offline player.
# Every other net check covers --accounts off (the default), which admits at once.
#   GODOT=<exe> [PORT=] tools/accountnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" accountnet
if ! docker info > /dev/null 2>&1; then echo "[accountnetcheck] RESULT: FAILED (no Docker: start Docker Desktop, the check needs a Nakama)"; exit 1; fi
. tools/nakama/throwaway.sh
nk_up unitsport-accountcheck 17360 17361 "$OUT/accountnet_nakama" \
  || { echo "[accountnetcheck] RESULT: FAILED (Nakama did not come up, $OUT/accountnet_nakama/up.log)"; exit 1; }
trap 'nk_down; _tc_cleanup' EXIT
PORT=${PORT:-7881}
WORLD="--world fixture --chunks fixture:flat"
SIGNIN=(--account-url "$NK_API" --account-key "$NK_SERVER_KEY" --account-email "rider$RANDOM@check.example" --account-password "check-pass-123")
LOGS=("$OUT/accountnet_offline.log" "$OUT/accountnet_online.log" "$OUT/accountnet_optional.log")

tc_server 300 120 "$OUT/accountnet_server_required.log" --server --port $PORT $WORLD --accounts required --nakama-url "$NK_API"
tc_client 200 "${LOGS[0]}" --connect 127.0.0.1:$PORT --name Offline $WORLD --accountprobe refuse & A=$!
tc_client 200 "${LOGS[1]}" --connect 127.0.0.1:$PORT --name Online $WORLD "${SIGNIN[@]}" --accountprobe admit & B=$!
wait $A $B
tc_stop

tc_server 300 120 "$OUT/accountnet_server_optional.log" --server --port $PORT $WORLD --accounts optional --nakama-url "$NK_API"
tc_client 200 "${LOGS[2]}" --connect 127.0.0.1:$PORT --name Offline $WORLD --accountprobe admit
tc_stop

grep -h "\[accounts\]\|\[accountprobe\]" "${LOGS[@]}" "$OUT"/accountnet_server_*.log
if tc_ok 3 "${LOGS[@]}"; then echo "[accountnetcheck] RESULT: ok"; exit 0; fi
echo "[accountnetcheck] RESULT: FAILED (see $OUT/accountnet_*.log)"; exit 1
