#!/usr/bin/env bash
# The server installs a newer release by itself, never under a player (#1088, src/Net/ServerAutoUpdate,
# src/Core/AutoUpdateNetProbe). A dedicated server pretending to run v0.0.1 (--fakeversion), with a stub
# update script (fetch: writes the pending tag) and a local release feed served over HTTP that is empty
# until admin A is in. The server must tell A that v9.9.9 is out and downloaded, a client of a newer
# protocol (B) must be told the server updates itself, and the restart must come only after A leaves.
# No terrain data needed; needs python for the feed.
#   GODOT=<exe> [PORT=] [FEEDPORT=] tools/autoupdatenetcheck.sh
. "$(dirname "$0")/lib/twoclient.sh" autoupdatenet
PORT=${PORT:-7889}
FEEDPORT=$(( ${FEEDPORT:-7893} + TC_PORT_OFFSET ))   # not an address the lib moves: moved here
DIR=$OUT/autoupdatenet
rm -rf "$DIR"; mkdir -p "$DIR/feed"
LEAVE=$DIR/leave
# the first python that runs (Windows' python3 can be the Store's stub)
PYTHON=; for p in python3 python; do "$p" -c "" 2>/dev/null && { PYTHON=$p; break; }; done
[ -n "$PYTHON" ] || { echo "[autoupdatenetcheck] RESULT: FAILED (no python for the release feed)"; exit 1; }

# the host script's stand-in: fetch marks the release pending, as update-server.sh does once it unpacked it
cat > "$DIR/update-stub.sh" <<EOF
#!/usr/bin/env bash
[ "\$1" = fetch ] || exit 2
echo "\$2" > "$DIR/pending-update"
echo "\$2 unpacked (stub)"
EOF
export UNITSPORT_UPDATER=$DIR/update-stub.sh
# Windows finds WSL's bash.exe in System32 before Git Bash: name this one (ServerUpdater reads UNITSPORT_BASH)
command -v cygpath > /dev/null && export UNITSPORT_BASH=$(cygpath -w "$(command -v bash)")

"$PYTHON" -m http.server "$FEEDPORT" --bind 127.0.0.1 --directory "$DIR/feed" > "$DIR/feed.log" 2>&1 & FEED=$!
trap 'kill $FEED 2>/dev/null; _tc_cleanup' EXIT

tc_server 300 120 "$OUT/autoupdatenet_server.log" --server --port $PORT --world fixture --chunks fixture:flat --admin-password test \
  --fakeversion 0.0.1 --updatefeed "http://127.0.0.1:$FEEDPORT/releases.json" --auto-update-minutes 0.05
tc_client 240 "$OUT/autoupdatenet_A.log" --connect 127.0.0.1:$PORT --name UpdateA --world fixture --chunks fixture:flat --autoupdatenet A "$LEAVE" & A=$!

# the release appears only once A is in: an empty server would restart at once
for i in $(seq 1 750); do grep -q "\[autoupdate A\] logged in" "$OUT/autoupdatenet_A.log" 2>/dev/null && break; kill -0 $A 2>/dev/null || break; guard_nap 0.2; done
cat > "$DIR/feed/releases.json" <<EOF
[{"tag_name":"v9.9.9","html_url":"http://127.0.0.1/","prerelease":false,"assets":[
  {"name":"UnitSportSwitzerland-v9.9.9-linux-x86_64.tar.gz","browser_download_url":"http://127.0.0.1:$FEEDPORT/none.tar.gz","size":104857600}]}]
EOF
for i in $(seq 1 600); do grep -q "is out and downloaded" "$OUT/autoupdatenet_A.log" 2>/dev/null && break; kill -0 $A 2>/dev/null || break; guard_nap 0.2; done

# a newer client meanwhile: refused, and told the server catches up by itself
tc_client 90 "$OUT/autoupdatenet_B.log" --connect 127.0.0.1:$PORT --name UpdateB --world fixture --chunks fixture:flat --autoupdatenet B --fakeprotocol 999
RESTARTED_EARLY=$(grep -c "ready: kicking everyone" "$OUT/autoupdatenet_server.log")
touch "$LEAVE"
wait $A
# A gone, nobody plays: the server quits for the update by itself
for i in $(seq 1 150); do kill -0 $SERVER 2>/dev/null || break; guard_nap 0.2; done
STILL_UP=0; kill -0 $SERVER 2>/dev/null && STILL_UP=1
tc_stop

grep -h "\[autoupdate\|\[update\]" "$OUT/autoupdatenet_server.log" "$OUT/autoupdatenet_A.log" "$OUT/autoupdatenet_B.log"
fail=
[ "$RESTARTED_EARLY" -eq 0 ] || fail="$fail restarted while A played;"
[ "$STILL_UP" -eq 0 ] || fail="$fail did not restart once empty;"
grep -q "v9.9.9 ready: kicking everyone" "$OUT/autoupdatenet_server.log" || fail="$fail no restart for v9.9.9;"
[ "$(cat "$DIR/pending-update" 2>/dev/null)" = v9.9.9 ] || fail="$fail the stub never fetched v9.9.9;"
tc_ok 2 "$OUT/autoupdatenet_A.log" "$OUT/autoupdatenet_B.log" || fail="$fail a probe failed;"
if [ -z "$fail" ]; then echo "[autoupdatenetcheck] RESULT: ok"; exit 0; fi
echo "[autoupdatenetcheck] RESULT: FAILED ($fail see $OUT/autoupdatenet_*.log)"; exit 1
