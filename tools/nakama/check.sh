#!/usr/bin/env bash
# Checks the Nakama stack and module (#1121) on a throwaway compose project, never the one up.sh runs:
# its own keys, ports 17350/17351 and volume, all deleted at the end. Needs Docker and curl. Not in tools/test.sh
# (no Docker there). Prints RESULT: PASS or RESULT: FAIL <why> as its last line.
set -euo pipefail
cd "$(dirname "$0")"
OUT=../../test_output/nakama-check
. ./throwaway.sh
fail() { echo "RESULT: FAIL $*"; exit 1; }
rand() { _nk_rand "$1"; }
nk_up unitsport-check 17350 17351 "$OUT" || fail "stack did not come up healthy (test_output/nakama-check/up.log)"
API=$NK_API HTTP_KEY=$NK_HTTP_KEY SERVER_KEY=$NK_SERVER_KEY

# call METHOD PATH [BODY] [CURL ARGS...]: prints "<status> <body>"
call() { local m=$1 p=$2 b=${3:-}; shift 3 || shift $#
  curl -s -o "$OUT/body" -w '%{http_code}' -X "$m" -H 'Content-Type: application/json' ${b:+--data "$b"} "$@" "$API$p"
  printf ' '; cat "$OUT/body"; echo; }
rpc() { call POST "/v2/rpc/$1?http_key=$HTTP_KEY&unwrap" "$2"; }

r=$(rpc healthcheck '{}'); echo "healthcheck: $r"
[[ $r == 200*'"ok":true'* ]] || fail "healthcheck: $r"

KEY=check$(rand 8)
for t in 60000 55000 70000; do
  r=$(rpc submit_result "{\"course\":\"check\",\"playerKey\":\"$KEY\",\"name\":\"Checker\",\"timeMs\":$t,\"avgWatts\":250}")
  echo "submit $t: $r"; [[ $r == 200* ]] || fail "submit_result $t: $r"
done
[[ $r == *'"best":55000'* ]] || fail "best time is not the fastest (55000): $r"

r=$(rpc submit_result '{"course":"Bad Course!","playerKey":"'"$KEY"'","timeMs":1}'); echo "invalid course: $r"
[[ $r == 400* ]] || fail "an invalid course was not refused with 400: $r"

# a player's own session: it reads the board, but may never write a result
r=$(call POST '/v2/account/authenticate/device?create=true' "{\"id\":\"device-$(rand 8)\"}" -u "$SERVER_KEY:")
[[ $r == 200* ]] || fail "device auth: $r"
TOKEN=$(sed -n 's/.*"token":"\([^"]*\)".*/\1/p' "$OUT/body")
r=$(call GET '/v2/leaderboard/course%3Acheck?limit=10' '' -H "Authorization: Bearer $TOKEN"); echo "board: $r"
[[ $r == 200*'"score":"55000"'* && $r == *'"username":"Checker"'* ]] || fail "board does not hold the best time: $r"
r=$(call POST '/v2/rpc/submit_result?unwrap' "{\"course\":\"check\",\"playerKey\":\"$KEY\",\"timeMs\":1}" -H "Authorization: Bearer $TOKEN")
echo "client submit: $r"
[[ $r == 403* ]] || fail "a player's session wrote a result: $r"

# ---- the website's accounts and the admin RPCs (#1207) ----
signin() { call POST "/v2/account/authenticate/email?create=$3${4:+&username=$4}" "{\"email\":\"$1\",\"password\":\"$2\"}" -u "$SERVER_KEY:"; }
token() { sed -n 's/.*"token":"\([^"]*\)".*/\1/p' "$OUT/body"; }
srpc() { call POST "/v2/rpc/$2?unwrap" "$3" -H "Authorization: Bearer $1"; }

r=$(signin player@check.example check-pass-1 true PlayerOne); [[ $r == 200* ]] || fail "player sign-up: $r"; PT=$(token)
r=$(srpc "$PT" me '{}'); echo "player me: $r"; [[ $r == 200*'"admin":false'* ]] || fail "a player is not an admin: $r"
r=$(srpc "$PT" admin_users '{"q":""}'); echo "player admin_users: ${r:0:60}"; [[ $r == 403* ]] || fail "a player listed the users: $r"
r=$(signin admin@check.example check-pass-2 true TheAdmin); [[ $r == 200* ]] || fail "admin sign-up: $r"; AT=$(token)
r=$(srpc "$AT" me '{}'); echo "bootstrap me: $r"; [[ $r == 200*'"admin":true'* ]] || fail "the bootstrap email is not admin: $r"
r=$(srpc "$AT" admin_users '{"q":"player"}'); echo "search: ${r:0:120}"; [[ $r == 200*'"username":"PlayerOne"'* ]] || fail "search did not find the player: $r"
PID=$(sed -n 's/.*"id":"\([^"]*\)","username":"PlayerOne".*/\1/p' "$OUT/body")
[ -n "$PID" ] || fail "no player id in: $r"
r=$(srpc "$AT" admin_user "{\"id\":\"$PID\"}"); echo "view: ${r:0:120}"; [[ $r == 200*'"admin":false'* ]] || fail "view: $r"
r=$(srpc "$AT" admin_set_admin "{\"id\":\"$PID\",\"admin\":true}"); [[ $r == 200* ]] || fail "make admin: $r"
r=$(srpc "$PT" me '{}'); [[ $r == 200*'"admin":true'* ]] || fail "the player did not become admin: $r"
r=$(srpc "$AT" admin_set_admin "{\"id\":\"$PID\",\"admin\":false}"); [[ $r == 200* ]] || fail "remove admin: $r"
r=$(srpc "$PT" me '{}'); [[ $r == 200*'"admin":false'* ]] || fail "the player is still admin: $r"
AID=$(srpc "$AT" me '{}' >/dev/null; sed -n 's/.*"id":"\([^"]*\)".*/\1/p' "$OUT/body")
r=$(srpc "$AT" admin_set_admin "{\"id\":\"$AID\",\"admin\":false}"); echo "remove own rights: ${r:0:60}"; [[ $r == 400* ]] || fail "an admin removed their own rights: $r"
r=$(srpc "$AT" admin_ban "{\"id\":\"$PID\",\"ban\":true}"); [[ $r == 200* ]] || fail "ban: $r"
r=$(signin player@check.example check-pass-1 false); echo "banned sign-in: ${r:0:70}"; [[ $r != 200* ]] || fail "a banned player signed in: $r"
r=$(srpc "$AT" admin_ban "{\"id\":\"$PID\",\"ban\":false}"); [[ $r == 200* ]] || fail "unban: $r"
r=$(signin player@check.example check-pass-1 false); [[ $r == 200* ]] || fail "an unbanned player cannot sign in: $r"

echo "RESULT: PASS"
