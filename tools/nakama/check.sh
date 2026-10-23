#!/usr/bin/env bash
# Checks the Nakama stack and module (#1121) on a throwaway compose project, never the one up.sh runs:
# its own keys, ports 17350/17351 and volume, all deleted at the end. Needs Docker and curl. Not in tools/test.sh
# (no Docker there). Prints RESULT: PASS or RESULT: FAIL <why> as its last line.
set -euo pipefail
cd "$(dirname "$0")"
OUT=../../test_output/nakama-check; mkdir -p "$OUT"
ENV="$OUT/.env"; P=unitsport-check; API=http://127.0.0.1:17350
rand() { od -An -tx1 -N"$1" /dev/urandom | tr -d ' \n'; }
HTTP_KEY=$(rand 24); SERVER_KEY=$(rand 24)
cat > "$ENV" <<EOF
POSTGRES_PASSWORD=$(rand 24)
NAKAMA_SERVER_KEY=$SERVER_KEY
NAKAMA_HTTP_KEY=$HTTP_KEY
NAKAMA_CONSOLE_USER=admin
NAKAMA_CONSOLE_PASSWORD=$(rand 24)
NAKAMA_ENCRYPTION_KEY=$(rand 24)
NAKAMA_PORT=17350
NAKAMA_CONSOLE_PORT=17351
EOF
dc() { docker compose -p $P --env-file "$ENV" "$@"; }
cleanup() { dc logs nakama > "$OUT/nakama.log" 2>&1 || true; dc down -v > /dev/null 2>&1 || true; }
trap cleanup EXIT
fail() { echo "RESULT: FAIL $*"; exit 1; }

dc up -d --wait > "$OUT/up.log" 2>&1 || fail "stack did not come up healthy (test_output/nakama-check/up.log)"

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

echo "RESULT: PASS"
