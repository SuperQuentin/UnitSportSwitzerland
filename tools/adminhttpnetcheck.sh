#!/usr/bin/env bash
# The game server's admin API for the website's dashboard (#1211, src/Net/AdminHttp + AdminApi,
# docs/notes/net/web-dashboard.md), against a throwaway Nakama (tools/nakama/throwaway.sh, so it needs Docker):
# - no token gets 401 and a player's token 403; the bootstrap admin's token gets in;
# - status, a command with its reply and the admin's name in the log, the players (a client joined), a kick by peer;
# - the startup arguments saved and a bad one refused;
# - a restart: the server quits and leaves the restart-requested marker start-server.sh starts again on.
#   GODOT=<exe> [PORT=] [APORT=] tools/adminhttpnetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" adminhttpnet
if ! docker info > /dev/null 2>&1; then echo "[adminhttpnetcheck] RESULT: FAILED (no Docker: start Docker Desktop, the check needs a Nakama)"; exit 1; fi
. tools/nakama/throwaway.sh
nk_up unitsport-admincheck 17370 17371 "$OUT/adminhttpnet_nakama" \
  || { echo "[adminhttpnetcheck] RESULT: FAILED (Nakama did not come up, $OUT/adminhttpnet_nakama/up.log)"; exit 1; }
trap 'nk_down; _tc_cleanup' EXIT
PORT=${PORT:-7884}
APORT=$(( ${APORT:-17790} + ${TC_PORT_OFFSET:-0} ))   # not an address the lib moves: moved here
WORLD=(--world fixture --chunks fixture:flat)
DIR=$OUT/adminhttpnet
rm -rf "$DIR"; mkdir -p "$DIR"
# the deploy directory start-server.sh exports: the startup file and the restart marker go there
export UNITSPORT_DEPLOY_DIR=$(cd "$DIR" && pwd)
command -v cygpath > /dev/null && UNITSPORT_DEPLOY_DIR=$(cygpath -w "$UNITSPORT_DEPLOY_DIR")
fail=
check() { echo "[adminhttpnetcheck] $1: ${2:-ok}"; [ -z "${2:-}" ] || fail="$fail $1: $2;"; }

# a session token, the account made on the fly (the admin's email is the throwaway's bootstrap one)
signup() {
  curl -s -u "$NK_SERVER_KEY:" -H 'Content-Type: application/json' -d "{\"email\":\"$1\",\"password\":\"check-pass-123\"}" \
    "$NK_API/v2/account/authenticate/email?create=true&username=$2" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p'
}
ADMIN=$(signup admin@check.example CheckAdmin)
PLAYER=$(signup "rider$RANDOM@check.example" CheckRider)
[ -n "$ADMIN" ] && [ -n "$PLAYER" ] || { echo "[adminhttpnetcheck] RESULT: FAILED (no session from Nakama)"; exit 1; }

# api <token> <path> [json body]: the body, then the status code on its own line
api() {
  local auth=(); [ -n "$1" ] && auth=(-H "Authorization: Bearer $1")
  if [ -n "${3:-}" ]; then curl -s -w '\n%{http_code}' "${auth[@]}" -H 'Content-Type: application/json' -d "$3" "http://127.0.0.1:$APORT$2"
  else curl -s -w '\n%{http_code}' "${auth[@]}" "http://127.0.0.1:$APORT$2"; fi
}
code() { tail -n 1 <<< "$1"; }

tc_server 300 120 "$OUT/adminhttpnet_server.log" --server --port $PORT "${WORLD[@]}" --admin-http $APORT --admin-nakama "$NK_API"
tc_client 240 "$OUT/adminhttpnet_client.log" --connect 127.0.0.1:$PORT --name Rider "${WORLD[@]}" & CLIENT=$!

r=$(api "" /status); [ "$(code "$r")" = 401 ] && check "no token" || check "no token" "got $(code "$r")"
r=$(api "$PLAYER" /status); [ "$(code "$r")" = 403 ] && check "a player's token" || check "a player's token" "got $(code "$r")"
r=$(api "$ADMIN" /status)
[ "$(code "$r")" = 200 ] && grep -q '"time":"It is' <<< "$r" && check "status" || check "status" "$r"

# the client in (it joins after loading the fixture)
PEER=
for _ in $(seq 1 450); do
  r=$(api "$ADMIN" /players); PEER=$(sed -n 's/.*"peer":\([0-9]*\),"name":"Rider".*/\1/p' <<< "$r"); [ -n "$PEER" ] && break
  kill -0 $CLIENT 2>/dev/null || break; guard_nap 0.2
done
[ -n "$PEER" ] && check "players: Rider is peer $PEER" || check "players" "Rider never listed: $r"

r=$(api "$ADMIN" /command '{"line":"who"}')
[ "$(code "$r")" = 200 ] && grep -q Rider <<< "$r" && check "command who" || check "command who" "$r"
r=$(api "$ADMIN" /command '{"line":"say hello from the dashboard check"}')
[ "$(code "$r")" = 200 ] && check "command say" || check "command say" "$r"
r=$(api "$ADMIN" "/log?after=0")
grep -q '\[admin\] CheckAdmin (website): /say hello from the dashboard check' <<< "$r" && check "log names the admin" || check "log names the admin" "not in /log"
grep -q '\[admin-http\] the website' <<< "$r" && check "log from the start" || check "log from the start" "no [admin-http] line"

r=$(api "$ADMIN" /startup '{"args":"--server-name   CheckServer"}')
[ "$(code "$r")" = 200 ] && [ "$(cat "$DIR/server-args.local")" = "--server-name CheckServer" ] && check "startup saved" || check "startup saved" "$r"
r=$(api "$ADMIN" /startup '{"args":"--port 1"}'); [ "$(code "$r")" = 400 ] && check "startup refuses --port" || check "startup refuses --port" "$r"

if [ -n "$PEER" ]; then
  r=$(api "$ADMIN" /kick "{\"peer\":$PEER,\"reason\":\"the check\"}")
  [ "$(code "$r")" = 200 ] && grep -q 'Rider was kicked' <<< "$r" && check "kick" || check "kick" "$r"
  for _ in $(seq 1 100); do grep -q '"players":\[\]' <<< "$(api "$ADMIN" /players)" && break; guard_nap 0.2; done
  grep -q '"players":\[\]' <<< "$(api "$ADMIN" /players)" && check "kicked player gone" || check "kicked player gone" "still listed"
fi
tc_stop $CLIENT

r=$(api "$ADMIN" /restart '{"minutes":0}')
[ "$(code "$r")" = 200 ] && check "restart asked" || check "restart asked" "$r"
for _ in $(seq 1 100); do kill -0 $SERVER 2>/dev/null || break; guard_nap 0.2; done
kill -0 $SERVER 2>/dev/null && check "server quit" "still running" || check "server quit"
grep -q CheckAdmin "$DIR/restart-requested" 2>/dev/null && check "restart marker" || check "restart marker" "no $DIR/restart-requested"
tc_stop

grep -h "\[admin" "$OUT/adminhttpnet_server.log" | head -20
if [ -z "$fail" ]; then echo "[adminhttpnetcheck] RESULT: ok"; exit 0; fi
echo "[adminhttpnetcheck] RESULT: FAILED ($fail see $OUT/adminhttpnet_*.log)"; exit 1
