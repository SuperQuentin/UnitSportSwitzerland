#!/usr/bin/env bash
# Invites over loopback (#840): a headless dedicated server on the fixture world and two clients.
# A asks B for a teleport (B taps F2), invites B to a party (B accepts as the Activities row does),
# opens a Battle Royale lobby from the Activities screen (B gets the invite to everyone and /accept br,
# and both share the party's team), challenges B to a fist fight (B accepts with E) and duels B in a
# race (B declines with Shift+F2); B holds F2 for the quick menu. Prints the RESULT line.
#   tools/invitecheck.sh            (GODOT = the editor executable, docs/notes/general/godot-exe.md)
#   SHOTS=1 tools/invitecheck.sh    (B windowed: test_output/invite_*.png, the toast, the quick menu, the screen)
. "$(dirname "$0")/lib/twoclient.sh" invitecheck
PORT=${INVITE_PORT:-7857}
WORLD="--chunks fixture:straight --traffic 0"
tc_server 300 120 $OUT/invite_server.log --server --port $PORT $WORLD --admin-password invitecheck --brbots 0
tc_client 260 $OUT/invite_A.log --connect 127.0.0.1:$PORT --name InviterA $WORLD --invitecheck A &
A=$!
WIN=; [ -n "${SHOTS:-}" ] && WIN=--windowed
tc_client 260 $OUT/invite_B.log $WIN --connect 127.0.0.1:$PORT --name InviterB $WORLD --nocapture --invitecheck B
wait $A
tc_stop
grep -h "\[invitecheck" $OUT/invite_A.log $OUT/invite_B.log
grep -h "\[invite\]\|\[party\]" $OUT/invite_server.log | head -40
code=0
tc_ok 2 $OUT/invite_A.log $OUT/invite_B.log || code=1
echo "[invitecheck] RESULT: $([ $code = 0 ] && echo ok || echo FAILED)"
exit $code
