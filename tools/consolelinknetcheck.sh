#!/usr/bin/env bash
# The remote console across versions (#1088, src/Net/ConsoleLink, src/Ui/ServerConsoleScreen,
# src/Ui/ConsoleLinkCheck): a dedicated server with an admin password, and one client from the title
# that speaks another protocol (--fakeprotocol). It is refused, opens the console from the refusal
# banner, is turned away with a wrong password, opens the link with the right one, runs who and say,
# and stays past the handshake's hello timeout. No terrain data needed.
#   GODOT=<exe> [PORT=] tools/consolelinknetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" consolelinknet
PORT=${PORT:-7887}
tc_server 240 120 "$OUT/consolelinknet_server.log" --server --port $PORT --world fixture --chunks fixture:flat --admin-password test --no-auto-update
tc_client 180 "$OUT/consolelinknet_A.log" --consolelinkcheck 127.0.0.1:$PORT --fakeprotocol 999 --world fixture --name ConsoleCheck
tc_stop
grep -h "\[consolelink\]\|\[console\]\|protocol 999" "$OUT/consolelinknet_server.log" "$OUT/consolelinknet_A.log"
if grep -q "never said which protocol" "$OUT/consolelinknet_server.log"; then
  echo "[consolelinknetcheck] RESULT: FAILED (the console peer was kicked by the hello timeout)"; exit 1
fi
if tc_ok 1 "$OUT/consolelinknet_A.log"; then echo "[consolelinknetcheck] RESULT: ok"; exit 0; fi
echo "[consolelinknetcheck] RESULT: FAILED (see $OUT/consolelinknet_*.log)"; exit 1
