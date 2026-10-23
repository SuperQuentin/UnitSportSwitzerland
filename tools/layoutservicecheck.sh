#!/usr/bin/env bash
# The house layout service end to end (#1186, docs/notes/net/house-layouts.md): log in by the link from the
# log, look up a building, claim it, save a layout, submit, approve as admin, and find it in the export.
# Usage: tools/layoutservicecheck.sh [chunks dir] [db]   (db: empty = SQLite, else LAYOUTS_DB, e.g. postgres://...)
# Needs python (picks a spot inside the outline) and the building tile of Rue du Rhone 10, Sion (buildings_2594_1120.bldg) in the chunks dir.
set -uo pipefail
cd "$(dirname "$0")/.."
CHUNKS=${1:-terrain_chunks}
OUT=test_output/layoutservice; rm -rf "$OUT"; mkdir -p "$OUT"
PORT=${PORT:-5097}; URL=http://127.0.0.1:$PORT
fail() { echo "[layoutservice] RESULT: FAILED $*"; [ -n "${PID:-}" ] && kill "$PID" 2>/dev/null; exit 1; }
dotnet build tools/LayoutService -v q -nologo >"$OUT/build.log" 2>&1 || fail "build (see $OUT/build.log)"
LAYOUTS_DATA="$OUT/data" LAYOUTS_DB="${2:-}" LAYOUTS_CHUNKS="$CHUNKS" LAYOUTS_URLS=$URL PUBLIC_URL=$URL \
  ADMIN_EMAILS=admin@example.ch COOKIE_SECURE=0 SMTP_HOST= \
  dotnet tools/LayoutService/bin/Debug/net8.0/LayoutService.dll >"$OUT/service.log" 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null' EXIT
for _ in $(seq 1 100); do curl -s "$URL/api/me" >/dev/null && break; sleep 0.2; done
H=(-H 'X-Requested-With: houses' -H 'Content-Type: application/json')

login() { # $1 email, $2 cookie jar: the link the service logged instead of mailing it
  curl -sf "${H[@]}" -d "{\"email\":\"$1\"}" "$URL/api/login" >/dev/null || fail "login $1"
  local tok=""
  for _ in $(seq 1 25); do tok=$(grep -o "login link for $1: .*#login=[A-Za-z0-9_-]*" "$OUT/service.log" | tail -1 | sed 's/.*#login=//'); [ -n "$tok" ] && break; sleep 0.2; done
  [ -n "$tok" ] || fail "no login link for $1 in the log"
  curl -sf "${H[@]}" -c "$2" -d "{\"token\":\"$tok\"}" "$URL/api/login/verify" >/dev/null || fail "verify $1"
  curl -s "${H[@]}" -d "{\"token\":\"$tok\"}" "$URL/api/login/verify" | grep -q expired || fail "a link logs in twice"
}

[ "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$URL/api/logout")" = 403 ] || fail "a POST without X-Requested-With went through"
login owner@example.ch "$OUT/owner.jar"
login admin@example.ch "$OUT/admin.jar"

# Rue du Rhone 10, Sion: EGID 954894 at the address point geo.admin.ch gives
B=$(curl -sf "$URL/api/building?egid=954894&e=2594004.3&n=1120149.1") || fail "building lookup"
echo "$B" > "$OUT/building.json"
EGID=$(echo "$B" | sed -n 's/.*"building":{"egid":\([0-9]*\).*/\1/p')
[ -n "$EGID" ] && [ "$EGID" != 0 ] || fail "no EGID in $B"
echo "$B" | grep -q '"claimable":true' || fail "not claimable: $B"

C=$(curl -sf "${H[@]}" -b "$OUT/owner.jar" -d "{\"egid\":$EGID,\"e\":2594004.3,\"n\":1120149.1,\"address\":\"Rue du Rhone 10, Sion\",\"note\":\"I live here\"}" "$URL/api/claims") || fail "claim"
ID=$(echo "$C" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
[ "$(curl -s -o /dev/null -w '%{http_code}' "${H[@]}" -b "$OUT/admin.jar" -d "{\"egid\":$EGID,\"e\":2594004.3,\"n\":1120149.1}" "$URL/api/claims")" = 409 ] \
  || fail "a second open claim on the same building"

# a 2 m room on a spot inside the real outline (the box's middle can be a courtyard)
CL=$(curl -sf -b "$OUT/owner.jar" "$URL/api/claims/$ID") || fail "read the claim"
read -r X0 Z0 X1 Z1 < <(echo "$CL" | python -c '
import json,sys
c=json.load(sys.stdin); rings=[[tuple(p) for p in r] for r in c["footprint"]]
def inside(x,z):
    k=False
    for r in rings:
        for i in range(len(r)):
            (ax,az),(bx,bz)=r[i],r[i-1]
            if (az>z)!=(bz>z) and x<(bx-ax)*(z-az)/(bz-az)+ax: k=not k
    return k
for i in range(-60,61):
    for j in range(-60,61):
        x,z=i*0.5,j*0.5
        if all(inside(x+dx,z+dz) for dx in (0,1,2) for dz in (0,1,2)): print(x,z,x+2,z+2); sys.exit()
') || fail "no 2 m spot inside the outline"
# the ground floor only: one room with a front door and a window
D0=$(awk -v a="$X0" 'BEGIN{print a+0.5}'); D1=$(awk -v a="$X0" 'BEGIN{print a+1.4}')
FL="{\"level\":0,\"rooms\":[{\"x0\":$X0,\"z0\":$Z0,\"x1\":$X1,\"z1\":$Z1,\"type\":\"Living\"}],\"openings\":[{\"kind\":\"window\",\"x0\":$X0,\"z0\":$Z0,\"x1\":$(awk -v a="$X0" 'BEGIN{print a+1}'),\"z1\":$Z0},{\"kind\":\"door\",\"x0\":$D0,\"z0\":$Z1,\"x1\":$D1,\"z1\":$Z1}]},"
BODY="{\"layout\":{\"storeyHeight\":2.7,\"floors\":[${FL%,}]},\"plan\":{\"scale\":0.01}}"
S=$(curl -sf -X PUT "${H[@]}" -b "$OUT/owner.jar" -d "$BODY" "$URL/api/claims/$ID/layout") || fail "save"
echo "$S" | grep -q '"problems":\[\]' || fail "the saved layout has problems: $S"
printf '\x89PNG\r\n\x1a\nnot really' > "$OUT/plan.png"; echo "<html>" > "$OUT/plan.html"
curl -sf "${H[@]/Content-Type: application\/json/}" -b "$OUT/owner.jar" -F "plan=@$OUT/plan.png" "$URL/api/claims/$ID/plan" >/dev/null || fail "plan upload"
[ "$(curl -s -o /dev/null -w '%{http_code}' -H 'X-Requested-With: houses' -b "$OUT/owner.jar" -F "plan=@$OUT/plan.html" "$URL/api/claims/$ID/plan")" = 400 ] || fail "an html plan was taken"
[ "$(curl -s -o /dev/null -w '%{http_code}' -b "$OUT/admin.jar" "$URL/api/claims/$ID/plan")" = 200 ] || fail "the admin cannot see the plan"
[ "$(curl -s -o /dev/null -w '%{http_code}' "$URL/api/claims/$ID/plan")" = 401 ] || fail "the plan is public"

curl -sf "${H[@]}" -b "$OUT/owner.jar" -X POST "$URL/api/claims/$ID/submit" >/dev/null || fail "submit"
[ "$(curl -s -o /dev/null -w '%{http_code}' "${H[@]}" -b "$OUT/owner.jar" -X POST "$URL/api/claims/$ID/approve")" = 404 ] || true
[ "$(curl -s -o /dev/null -w '%{http_code}' "${H[@]}" -b "$OUT/owner.jar" -X POST "$URL/api/admin/claims/$ID/approve")" = 403 ] || fail "the owner approved their own claim"
curl -sf -b "$OUT/admin.jar" "$URL/api/admin/claims" | grep -q "\"id\":$ID" || fail "not in the review queue"
curl -sf "${H[@]}" -b "$OUT/admin.jar" -X POST "$URL/api/admin/claims/$ID/approve" >/dev/null || fail "approve"
grep -q "\"egid\":$EGID" "$OUT/data/house-layouts.json" || fail "not in the export"
curl -sf "$URL/api/building?egid=954894&e=2594004.3&n=1120149.1" | grep -q '"approved":true' || fail "the building does not say approved"
curl -sf "${H[@]}" -b "$OUT/owner.jar" -X POST "$URL/api/claims/$ID/withdraw" >/dev/null || fail "withdraw"
grep -q "\"egid\":$EGID" "$OUT/data/house-layouts.json" && fail "still exported after the withdrawal"
echo "[layoutservice] RESULT: ok (EGID $EGID, $([ -n "${2:-}" ] && echo postgres || echo sqlite))"
